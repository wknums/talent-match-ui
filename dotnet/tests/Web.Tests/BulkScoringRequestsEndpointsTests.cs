using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.HostedServices;
using TalentMatch.Infrastructure.Persistence;
using WebClient = TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class BulkScoringRequestsEndpointsTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";
    private const string Large = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Large";
    private const string OneDrive = "https://tenant-a-my.sharepoint.com/personal/alex_tenant_a/Documents/Candidates";

    private static async Task<HttpResponseMessage> PreviewAsync(
        HttpClient client,
        string folderUrl,
        string? correlationId = null)
    {
        using var beginRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/jobs/job-1/source-connections")
        {
            Content = JsonContent.Create(new { folderUrl }),
        };
        if (correlationId is not null)
            beginRequest.Headers.Add("X-Correlation-ID", correlationId);
        using var begin = await client.SendAsync(beginRequest);
        if (begin.StatusCode != HttpStatusCode.Created)
            return new HttpResponseMessage(begin.StatusCode)
            {
                Content = new StringContent(await begin.Content.ReadAsStringAsync()),
            };

        var connectionId = (await begin.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionId")
            .GetString();
        using var previewRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/source-connections/{connectionId}/preview");
        if (correlationId is not null)
            previewRequest.Headers.Add("X-Correlation-ID", correlationId);
        return await client.SendAsync(previewRequest);
    }

    [Fact]
    public async Task When_off_capabilities_say_so_and_every_operation_answers_503()
    {
        using var factory = new BulkFactory(mode: null);
        using var client = await factory.LoginAsync("recruiter", "test123");

        var capabilities = await (await client.GetAsync("/api/bulk-scoring-requests/capabilities")).Content.ReadFromJsonAsync<JsonElement>();
        capabilities.GetProperty("enabled").GetBoolean().Should().BeFalse();

        var preview = await client.PostAsJsonAsync(
            "/api/jobs/job-1/source-connections",
            new { folderUrl = Candidates });
        preview.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("feature_unavailable");
    }

    [Fact]
    public async Task Capabilities_describe_the_environment_including_both_sources_and_the_documented_limits()
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");

        var caps = await (await client.GetAsync("/api/bulk-scoring-requests/capabilities")).Content.ReadFromJsonAsync<JsonElement>();

        caps.GetProperty("enabled").GetBoolean().Should().BeTrue();
        caps.GetProperty("mode").GetString().Should().Be("mock");
        caps.GetProperty("accessModel").GetString().Should().Be("delegated");
        caps.GetProperty("supportedSources").EnumerateArray().Select(x => x.GetString()).Should().Equal("sharepoint", "onedrive_business");
        caps.GetProperty("convertibleExtensions").EnumerateArray().Select(x => x.GetString()).Should().Contain([".pdf", ".txt", ".docx", ".md"]);
        var limits = caps.GetProperty("limits");
        limits.GetProperty("maxFilesPerRequest").GetInt32().Should().Be(30_000);
        limits.GetProperty("maxBytesPerFile").GetInt64().Should().Be(8_388_608);
        limits.GetProperty("discoveryStepFiles").GetInt32().Should().Be(1_000);
    }

    [Fact]
    public async Task Preview_confirm_and_page_the_included_files_for_a_sharepoint_folder()
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");

        var preview = await PreviewAsync(client, Candidates);
        preview.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("requestId").GetString()!;
        var progress = body.GetProperty("progress");
        progress.GetProperty("discoveryStatus").GetString().Should().Be("complete");
        progress.GetProperty("includedCount").GetInt32().Should().Be(11);
        progress.GetProperty("excludedCount").GetInt32().Should().Be(2);
        progress.GetProperty("excludedByReason").GetProperty("unsupported_type").GetInt32().Should().Be(1);
        progress.GetProperty("excludedByReason").GetProperty("file_too_large").GetInt32().Should().Be(1);
        progress.GetProperty("inaccessibleItemCount").GetInt32().Should().Be(1);
        progress.GetProperty("includedCountIfRasterized").GetInt32().Should().Be(11);
        body.GetProperty("included").GetArrayLength().Should().Be(11);
        body.ToString().Should().NotContain("secret.pdf", "inaccessible names never surface").And.NotContain("archive.zip", "excluded entries are never listed");

        var items = await (await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?pageSize=5&page=3")).Content.ReadFromJsonAsync<JsonElement>();
        items.GetProperty("total").GetInt32().Should().Be(11);
        items.GetProperty("items").GetArrayLength().Should().Be(1);

        var confirm = await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/confirm", new { convertToRasterPdf = false, allowDuplicates = false });
        confirm.StatusCode.Should().Be(HttpStatusCode.Created);
        var accepted = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        accepted.GetProperty("status").GetString().Should().Be("active");
        accepted.GetProperty("sourceKind").GetString().Should().Be("sharepoint");
        accepted.GetProperty("counts").GetProperty("discovered").GetInt32().Should().Be(11);
        accepted.GetProperty("counts").GetProperty("waiting").GetInt32().Should().Be(11);
        accepted.GetProperty("authorizationState").GetString().Should().Be("active");

        var replay = await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/confirm", new { convertToRasterPdf = true, allowDuplicates = true });
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("convertToRasterPdf").GetBoolean().Should().BeFalse("choices are fixed at the first confirm");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ProcessingEvents.Select(e => e.EventType).Should().Contain("bulk-scoring-request.accepted");
        db.BulkRequestAuthorizations.Count().Should().Be(1);
    }

    [Fact]
    public async Task A_onedrive_folder_works_the_same_way_and_a_folder_the_user_cannot_open_is_refused()
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");

        var ok = await PreviewAsync(client, OneDrive);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ok.Content.ReadFromJsonAsync<JsonElement>();
        created.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(3);

        var denied = await PreviewAsync(
            client,
            "https://tenant-a-my.sharepoint.com/personal/sam_tenant_a/Documents/Private");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await denied.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("code").GetString().Should().Be("sharepoint_access_denied");
        error.ToString().Should().NotContain("jon.pdf");
    }

    [Theory]
    [InlineData("https://1drv.ms/f/s!AbCdEf")]
    [InlineData("https://onedrive.live.com/?id=ABC")]
    [InlineData("https://files.example.com/Candidates")]
    [InlineData("not a url")]
    public async Task Personal_and_unknown_links_are_rejected_before_anything_is_read(string url)
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");

        var response = await client.PostAsJsonAsync(
            "/api/jobs/job-1/source-connections",
            new { folderUrl = url });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should()
            .Be(Uri.TryCreate(url, UriKind.Absolute, out _)
                ? "source_folder_invalid_or_unavailable"
                : "validation_failed");
    }

    [Fact]
    public async Task A_fully_scanned_folder_with_nothing_eligible_creates_no_draft_and_unknown_jobs_are_404()
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");

        var empty = await PreviewAsync(
            client,
            "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates/Confidential");
        empty.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the recruiter cannot open that folder");

        var noJob = await client.PostAsJsonAsync(
            "/api/jobs/missing/source-connections",
            new { folderUrl = Candidates });
        noJob.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().BulkScoringRequests.Count().Should().Be(0);
    }

    [Fact]
    public async Task Large_folders_are_discovered_in_steps_until_complete_and_then_refuse_more()
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");

        var preview = await (await PreviewAsync(client, Large)).Content.ReadFromJsonAsync<JsonElement>();
        var id = preview.GetProperty("requestId").GetString()!;
        preview.GetProperty("included").GetArrayLength().Should().Be(100, "only the first page is returned");
        preview.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(1_000);
        preview.GetProperty("progress").GetProperty("discoveryStatus").GetString().Should().Be("more_available");

        var more1 = await (await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null)).Content.ReadFromJsonAsync<JsonElement>();
        more1.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(2_000);
        var more2 = await (await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null)).Content.ReadFromJsonAsync<JsonElement>();
        more2.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(2_500);
        more2.GetProperty("progress").GetProperty("discoveryStatus").GetString().Should().Be("complete");
        more2.GetProperty("progress").GetProperty("excludedCount").GetInt32().Should().Be(20_000);

        var finished = await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null);
        finished.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await finished.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("discovery_finished");
    }

    [Fact]
    public async Task A_preview_can_be_abandoned_and_belongs_to_the_recruiter_who_started_it()
    {
        using var factory = new BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        using var other = await factory.LoginAsync("recruiter2", "test123");
        var id = (await (await PreviewAsync(owner, Candidates)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requestId").GetString()!;

        (await other.GetAsync($"/api/bulk-scoring-requests/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/bulk-scoring-requests/{id}/items")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.DeleteAsync($"/api/bulk-scoring-requests/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await owner.GetAsync($"/api/bulk-scoring-requests/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.DeleteAsync($"/api/bulk-scoring-requests/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetAsync($"/api/bulk-scoring-requests/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Missing_bulk_request_is_an_Rfc9457_problem_with_the_same_correlation_ID()
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/bulk-scoring-requests/{Guid.NewGuid()}");
        request.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle()
            .Which.Should().Be(correlationId);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.EnumerateObject().Select(property => property.Name).Should().BeSubsetOf(
            ["type", "title", "status", "detail", "code", "correlationId", "adminConsentUrl"]);
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("code").GetString().Should().Be("not_found");
        problem.GetProperty("correlationId").GetString().Should().Be(correlationId);
    }

    [Fact]
    public async Task Preview_discovery_and_confirmation_commit_every_applicable_audit_row()
    {
        using var factory = new BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");
        var correlationId = Guid.NewGuid().ToString();
        using var previewResponse = await PreviewAsync(client, Candidates, correlationId);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = preview.GetProperty("requestId").GetString()!;
        using var confirmRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/bulk-scoring-requests/{requestId}/confirm")
        {
            Content = JsonContent.Create(new
            {
                convertToRasterPdf = false,
                allowDuplicates = false,
            }),
        };
        confirmRequest.Headers.Add("X-Correlation-ID", correlationId);

        using var confirmResponse = await client.SendAsync(confirmRequest);

        confirmResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.Set<BulkAuditEvent>()
            .Where(row => row.CorrelationId == correlationId)
            .ToListAsync();
        var actorId = await db.Users
            .Where(user => user.Username == "recruiter")
            .Select(user => user.Id)
            .SingleAsync();
        audit.Select(row => row.EventType).Should().Contain(
            BulkAuditEventTypes.PreviewDraftCreated,
            BulkAuditEventTypes.PreviewDiscoveryCommitted,
            BulkAuditEventTypes.BulkItemDiscovered,
            BulkAuditEventTypes.BulkRequestConfirmed,
            BulkAuditEventTypes.SourceConnectionConsumed,
            BulkAuditEventTypes.RequestAuthorizationEstablished);
        audit.Count(row => row.EventType == BulkAuditEventTypes.BulkItemDiscovered).Should().Be(11);
        audit.Should().OnlyContain(row =>
            row.ActorType == BulkAuditActorType.User
            && row.ActorId == actorId
            && row.OccurredAt.Kind == DateTimeKind.Utc
            && row.SchemaVersion > 0);
    }

    [Fact]
    public async Task Admin_settings_default_validate_version_and_are_admin_only()
    {
        using var factory = new BulkFactory("mock");
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        using var recruiter = await factory.LoginAsync("recruiter", "test123");

        var initial = await (await admin.GetAsync("/api/bulk-scoring-settings")).Content.ReadFromJsonAsync<JsonElement>();
        initial.GetProperty("maxFilesPerRequest").GetInt32().Should().Be(30_000);
        initial.GetProperty("concurrencyVersion").GetInt32().Should().Be(0);

        (await admin.PutAsJsonAsync("/api/bulk-scoring-settings", new { maxFilesPerRequest = 0, expectedConcurrencyVersion = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var saved = await admin.PutAsJsonAsync("/api/bulk-scoring-settings", new { maxFilesPerRequest = 1_500, expectedConcurrencyVersion = 0 });
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("concurrencyVersion").GetInt32().Should().Be(1);
        var stale = await admin.PutAsJsonAsync("/api/bulk-scoring-settings", new { maxFilesPerRequest = 2_000, expectedConcurrencyVersion = 0 });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await recruiter.GetAsync("/api/bulk-scoring-settings")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.Found);
        (await recruiter.PutAsJsonAsync("/api/bulk-scoring-settings", new { maxFilesPerRequest = 5, expectedConcurrencyVersion = 1 }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.Found);

        // A new request snapshots the saved maximum and stops exactly at it.
        var preview = await (await PreviewAsync(recruiter, Large)).Content.ReadFromJsonAsync<JsonElement>();
        var id = preview.GetProperty("requestId").GetString()!;
        var more = await (await recruiter.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null)).Content.ReadFromJsonAsync<JsonElement>();
        more.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(1_500);
        more.GetProperty("progress").GetProperty("discoveryStatus").GetString().Should().Be("limit_reached");
        (await recruiter.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_typed_client_round_trips_every_operation_against_the_real_server()
    {
        using var factory = new BulkFactory("mock");
        using var recruiterHttp = await factory.LoginAsync("recruiter", "test123");
        var recruiter = new WebClient.ApiClient(recruiterHttp);

        var capabilities = await recruiter.GetBulkScoringCapabilitiesAsync();
        capabilities.Enabled.Should().BeTrue();
        capabilities.SupportedSources.Should().Equal("sharepoint", "onedrive_business");
        capabilities.ConvertibleExtensions.Should().Contain(".txt");
        capabilities.Limits.MaxFilesPerRequest.Should().Be(30_000);

        var connection = await recruiter.BeginSourceConnectionAsync("job-1", Candidates);
        var preview = await recruiter.PreviewSourceConnectionAsync(connection.ConnectionId);
        preview.DiscoveryStatus.Should().Be("complete");
        preview.Counts.Discovered.Should().Be(11);
        preview.ExcludedByReason.Should().ContainKey("unsupported_type").And.ContainKey("file_too_large");
        preview.Counts.Unreadable.Should().Be(1);
        preview.FolderName.Should().NotBeNullOrWhiteSpace();

        var page = await recruiter.GetBulkScoringRequestItemsAsync(preview.RequestId, page: 2, pageSize: 5);
        (page.Total, page.Page, page.Items.Count).Should().Be((11, 2, 5));
    }

    [Fact]
    public async Task The_typed_client_surfaces_the_error_code_so_the_dialog_can_react_to_it()
    {
        using var factory = new BulkFactory("mock");
        using var http = await factory.LoginAsync("recruiter", "test123");
        var api = new WebClient.ApiClient(http);

        var unsupported = await FluentActions.Awaiting(() => api.BeginSourceConnectionAsync("job-1", "https://1drv.ms/f/abc"))
            .Should().ThrowAsync<WebClient.ApiException>();
        unsupported.Which.StatusCode.Should().Be(422);
        unsupported.Which.ErrorCode.Should().Be("source_folder_invalid_or_unavailable");

        var connection = await api.BeginSourceConnectionAsync("job-1", Candidates);
        var preview = await api.PreviewSourceConnectionAsync(connection.ConnectionId);
        var confirmed = await api.ConfirmBulkScoringRequestAsync(preview.RequestId, convertToRasterPdf: true, allowDuplicates: false);
        confirmed.Status.Should().Be("active");
        confirmed.ConvertToRasterPdf.Should().BeTrue();
        confirmed.Counts.Discovered.Should().Be(11);
        (await api.GetBulkScoringRequestAsync(preview.RequestId)).RequestId.Should().Be(preview.RequestId);

        var completeAlready = await FluentActions.Awaiting(() => api.DiscoverMoreBulkScoringRequestAsync(preview.RequestId))
            .Should().ThrowAsync<WebClient.ApiException>();
        completeAlready.Which.StatusCode.Should().Be(409);
        completeAlready.Which.ErrorCode.Should().NotBeNullOrWhiteSpace();

        var secondConnection = await api.BeginSourceConnectionAsync("job-1", Candidates);
        var second = await api.PreviewSourceConnectionAsync(secondConnection.ConnectionId);
        await api.AbandonBulkScoringRequestDraftAsync(second.RequestId);
        await api.AbandonBulkScoringRequestDraftAsync(second.RequestId);
    }

    [Fact]
    public async Task The_typed_client_reads_and_saves_the_admin_setting_and_reports_a_stale_version()
    {
        using var factory = new BulkFactory("mock");
        using var http = await factory.LoginAsync("admin", "adm1n99");
        var api = new WebClient.ApiClient(http);

        (await api.GetBulkScoringSettingsAsync()).MaxFilesPerRequest.Should().Be(30_000);
        var saved = await api.UpdateBulkScoringSettingsAsync(new WebClient.UpdateBulkSettingsRequest(2_500, 0));
        (saved.MaxFilesPerRequest, saved.ConcurrencyVersion).Should().Be((2_500, 1));

        var stale = await FluentActions.Awaiting(() => api.UpdateBulkScoringSettingsAsync(new WebClient.UpdateBulkSettingsRequest(3_000, 0)))
            .Should().ThrowAsync<WebClient.ApiException>();
        stale.Which.StatusCode.Should().Be(409);
    }

    internal sealed class BulkFactory : WebApplicationFactory<Program>
    {
        private readonly string? _mode;
        private readonly IReadOnlyDictionary<string, string>? _settings;
        private readonly Action<IServiceCollection>? _configure;
        private readonly bool _runWorker;
        private readonly string? _databasePath;
        private SqliteConnection? _connection;

        public BulkFactory(string? mode, IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? configure = null,
            bool runWorker = false, string? databasePath = null)
        {
            _mode = mode;
            _settings = settings;
            _configure = configure;
            _runWorker = runWorker;
            _databasePath = databasePath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (_mode is not null)
            {
                builder.UseSetting("MICROSOFT365_SOURCE_MODE", _mode);
                builder.UseSetting("MICROSOFT365_SOURCE_PUBLISHER_VERIFIED", "true");
                builder.UseSetting("SHAREPOINT_ALLOWED_HOSTS", "contoso.sharepoint.com");
                builder.UseSetting("ONEDRIVE_ALLOWED_HOSTS", "contoso-my.sharepoint.com");
            }

            foreach (var (key, value) in _settings ?? new Dictionary<string, string>())
                builder.UseSetting(key, value);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                // The endpoints are under test, not the worker sharing the in-memory connection.
                var worker = services.FirstOrDefault(d => d.ImplementationType == typeof(BulkScoringWorker));
                if (worker is not null && !_runWorker) services.Remove(worker);
                _configure?.Invoke(services);

                if (_databasePath is null)
                {
                    _connection = new SqliteConnection("Data Source=:memory:");
                    _connection.Open();
                    services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                }
                else
                {
                    services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath};Default Timeout=60"));
                }

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
                db.Users.AddRange(
                    new User { Username = "admin", Role = "admin", FullName = "Administrator", Department = "all", PasswordHash = Hash("adm1n99") },
                    new User { Username = "recruiter", Role = "recruiter", FullName = "Recruiter", Department = "Engineering", PasswordHash = Hash("test123") },
                    new User { Username = "recruiter2", Role = "recruiter", FullName = "Recruiter Two", Department = "Engineering", PasswordHash = Hash("test123") });
                var job = new Job { Id = "job-1", Title = "Job" };
                db.Jobs.Add(job);
                db.SaveChanges();
                var config = new JobConfigVersion
                {
                    Id = "cfg-1",
                    JobId = job.Id,
                    RubricApprovalStatus = "approved",
                };
                db.JobConfigVersions.Add(config);
                db.SaveChanges();
                job.CurrentConfigVersionId = config.Id;
                db.SaveChanges();
            });
        }

        public async Task<HttpClient> LoginAsync(string username, string password)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            (await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password })).StatusCode.Should().Be(HttpStatusCode.OK);
            return client;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }
}
