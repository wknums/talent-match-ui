using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.SharePoint;

namespace TalentMatch.Web.Tests;

// Discovery, confirm and access behaviour of the bulk scoring endpoints, end to end against the real server and mock source.
public sealed class BulkScoringAcceptanceEndpointsTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";
    private const string Archive = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates/Archive";
    private const string Large = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Large";
    private const string OneDrive = "https://tenant-a-my.sharepoint.com/personal/alex_tenant_a/Documents/Candidates";
    private const string Recruiter = "fixture-account-recruiter";

    private static BulkScoringRequestsEndpointsTests.BulkFactory SameTenant(Dictionary<string, string>? settings = null, Action<IServiceCollection>? configure = null) =>
        new("mock", settings, configure);

    private static BulkScoringRequestsEndpointsTests.BulkFactory CrossTenant() => new("mock");

    private static async Task<JsonElement> Preview(HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var begin = await client.PostAsJsonAsync(
            "/api/jobs/job-1/source-connections",
            new { folderUrl = url });
        begin.StatusCode.Should().Be(HttpStatusCode.Created);
        var connectionId = (await begin.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionId")
            .GetString();
        var response = await client.PostAsync(
            $"/api/source-connections/{connectionId}/preview",
            null);
        response.StatusCode.Should().Be(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, string id, bool raster = false, bool duplicates = false) =>
        client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/confirm", new { convertToRasterPdf = raster, allowDuplicates = duplicates });

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    // ---- discovery

    [Fact]
    public async Task A_draft_with_nothing_eligible_yet_cannot_be_confirmed_until_discovery_finds_a_file()
    {
        using var factory = SameTenant(new() { ["BULK_DISCOVERY_STEP_MAX_ENTRIES"] = "500" });
        using var client = await factory.LoginAsync("recruiter", "test123");

        var preview = await Preview(client, Large);
        var id = preview.GetProperty("requestId").GetString()!;
        preview.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(0);
        preview.GetProperty("progress").GetProperty("discoveryStatus").GetString().Should().Be("more_available");
        preview.GetProperty("included").GetArrayLength().Should().Be(0);

        var refused = await Confirm(client, id);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Code(refused)).Should().Be("validation_failed");

        var included = 0;
        for (var step = 0; step < 10 && included == 0; step++)
        {
            var more = await (await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null)).Content.ReadFromJsonAsync<JsonElement>();
            included = more.GetProperty("progress").GetProperty("includedCount").GetInt32();
        }

        included.Should().BeGreaterThan(0, "scanning on eventually reaches eligible files");
        (await Confirm(client, id)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Every_discovery_step_extends_the_drafts_expiry()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var preview = await Preview(client, Large);
        var id = preview.GetProperty("requestId").GetString()!;
        var first = preview.GetProperty("expiresAt").GetDateTime();

        await Task.Delay(50);
        var more = await (await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null)).Content.ReadFromJsonAsync<JsonElement>();

        more.GetProperty("expiresAt").GetDateTime().Should().BeAfter(first);
    }

    [Fact]
    public async Task The_per_file_limit_comes_from_system_configuration_and_stays_fixed_on_each_request()
    {
        using var factory = SameTenant();
        using var admin = await factory.LoginAsync("admin", "adm1n99");
        using var recruiter = await factory.LoginAsync("recruiter", "test123");

        var before = await Preview(recruiter, Candidates);
        before.GetProperty("limits").GetProperty("maxBytesPerFile").GetInt64().Should().Be(8_388_608, "the default is 8 MiB");
        before.GetProperty("progress").GetProperty("excludedByReason").GetProperty("file_too_large").GetInt32().Should().Be(1);
        before.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(11);

        (await admin.PutAsJsonAsync("/api/admin/upload-settings", new
        {
            fileConcurrency = 4,
            maxIndividualFileBytes = 20_000_000,
            maxInFlightBytes = 40_000_000,
            expectedConcurrencyVersion = 0,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Preview(recruiter, Candidates);
        after.GetProperty("limits").GetProperty("maxBytesPerFile").GetInt64().Should().Be(20_000_000);
        after.GetProperty("progress").GetProperty("excludedByReason").GetProperty("file_too_large").GetInt32().Should().Be(0);
        after.GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(12, "the 9 MiB file now fits");

        var running = await (await recruiter.GetAsync($"/api/bulk-scoring-requests/{before.GetProperty("requestId").GetString()}")).Content.ReadFromJsonAsync<JsonElement>();
        running.GetProperty("limits").GetProperty("maxBytesPerFile").GetInt64().Should().Be(8_388_608, "an existing request keeps the limits it started with");
    }

    // ---- confirm

    [Fact]
    public async Task An_expired_preview_answers_410_for_discover_more_and_for_confirm()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = (await Preview(client, Large)).GetProperty("requestId").GetString()!;
        await ExpireAsync(factory, id);

        var more = await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null);
        more.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await Code(more)).Should().Be("draft_expired");

        var confirm = await Confirm(client, id);
        confirm.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await Code(confirm)).Should().Be("draft_expired");
    }

    [Fact]
    public async Task Only_the_files_found_before_confirming_are_imported()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var preview = await Preview(client, Large);
        var id = preview.GetProperty("requestId").GetString()!;
        preview.GetProperty("progress").GetProperty("discoveryStatus").GetString().Should().Be("more_available");

        var accepted = await (await Confirm(client, id)).Content.ReadFromJsonAsync<JsonElement>();

        accepted.GetProperty("counts").GetProperty("discovered").GetInt32().Should().Be(1_000, "the other 1,500 were never discovered");
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().BulkScoringItems.Count(i => i.RequestId == id).Should().Be(1_000);
        (await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "an accepted request cannot be extended");
    }

    [Fact]
    public async Task The_raster_and_duplicate_choices_are_recorded_at_confirm_and_cannot_change_afterwards()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = (await Preview(client, Candidates)).GetProperty("requestId").GetString()!;

        var accepted = await (await Confirm(client, id, raster: true, duplicates: true)).Content.ReadFromJsonAsync<JsonElement>();
        accepted.GetProperty("convertToRasterPdf").GetBoolean().Should().BeTrue();
        accepted.GetProperty("allowDuplicates").GetBoolean().Should().BeTrue();

        var replay = await Confirm(client, id, raster: false, duplicates: false);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var unchanged = await replay.Content.ReadFromJsonAsync<JsonElement>();
        unchanged.GetProperty("convertToRasterPdf").GetBoolean().Should().BeTrue();
        unchanged.GetProperty("allowDuplicates").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_job_configuration_version_is_recorded_and_a_later_change_is_only_a_notice()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        await SetConfigVersionAsync(factory, "cfg-1");
        var id = (await Preview(client, Candidates)).GetProperty("requestId").GetString()!;
        var accepted = await (await Confirm(client, id)).Content.ReadFromJsonAsync<JsonElement>();
        accepted.GetProperty("capturedScoringConfigVersionId").GetString().Should().Be("cfg-1");
        accepted.GetProperty("configChangedSinceAcceptance").GetBoolean().Should().BeFalse();

        await SetConfigVersionAsync(factory, "cfg-2");

        var later = await (await client.GetAsync($"/api/bulk-scoring-requests/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        later.GetProperty("configChangedSinceAcceptance").GetBoolean().Should().BeTrue();
        later.GetProperty("capturedScoringConfigVersionId").GetString().Should().Be("cfg-1");
        later.GetProperty("counts").GetProperty("failed").GetInt32().Should().Be(0, "a configuration change never fails items");
        later.GetProperty("counts").GetProperty("waiting").GetInt32().Should().Be(11);
    }

    // ---- access: same tenant

    [Fact]
    public async Task Confirm_checks_again_that_the_recruiter_can_still_open_the_folder()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = (await Preview(client, Candidates)).GetProperty("requestId").GetString()!;

        factory.Services.GetRequiredService<MockSharePointSource>().Revoke(Recruiter, "sp-candidates");

        var confirm = await Confirm(client, id);
        confirm.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("code").GetString().Should().Be("sharepoint_access_denied");
        error.ToString().Should().NotContain("alice-1page.pdf", "no names in the refusal");
        var draft = await (await client.GetAsync($"/api/bulk-scoring-requests/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        draft.GetProperty("status").GetString().Should().Be("draft", "nothing was accepted");
    }

    [Fact]
    public async Task Discover_more_does_not_read_what_the_recruiter_can_no_longer_open()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var preview = await Preview(client, Large);
        var id = preview.GetProperty("requestId").GetString()!;
        var includedBefore = preview.GetProperty("progress").GetProperty("includedCount").GetInt32();

        factory.Services.GetRequiredService<MockSharePointSource>().Revoke(Recruiter, "sp-large");

        var response = await client.PostAsync($"/api/bulk-scoring-requests/{id}/discover-more", null);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var progress = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("progress");
            progress.GetProperty("includedCount").GetInt32().Should().Be(includedBefore, "nothing new is included");
            progress.GetProperty("inaccessibleItemCount").GetInt32().Should().BeGreaterThan(0, "the refused items are counted, never named");
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Code(response)).Should().Be("sharepoint_access_denied");
        }
    }

    [Fact]
    public async Task When_the_authorization_cannot_be_stored_the_draft_stays_unconfirmed_and_the_recruiter_is_asked_to_sign_in()
    {
        using var factory = SameTenant(configure: services =>
        {
            services.RemoveAll<IRequestAuthorizationStore>();
            services.AddScoped<IRequestAuthorizationStore, CannotStoreAuthorization>();
        });
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = (await Preview(client, Candidates)).GetProperty("requestId").GetString()!;

        var confirm = await Confirm(client, id);

        confirm.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(confirm)).Should().Be("authorization_required");
        var draft = await (await client.GetAsync($"/api/bulk-scoring-requests/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        draft.GetProperty("status").GetString().Should().Be("draft");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BulkRequestAuthorizations.Count().Should().Be(0);
        db.ProcessingEvents.Select(e => e.EventType).Should().NotContain("bulk-scoring-request.accepted");
    }

    [Fact]
    public async Task A_delegated_request_records_its_connection_and_an_active_authorization()
    {
        using var factory = SameTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = (await Preview(client, Candidates)).GetProperty("requestId").GetString()!;

        var accepted = await (await Confirm(client, id)).Content.ReadFromJsonAsync<JsonElement>();

        accepted.GetProperty("accessModel").GetString().Should().Be("delegated");
        accepted.GetProperty("authorizationState").GetString().Should().Be("active");
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().BulkScoringRequests
            .Single(r => r.Id == id).ConnectionId.Should().NotBeNullOrWhiteSpace();
    }

    // ---- access: cross tenant

    [Fact]
    public async Task Delegated_flow_offers_sharepoint_and_work_onedrive_without_tenant_modes()
    {
        using var factory = CrossTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");

        var caps = await (await client.GetAsync("/api/bulk-scoring-requests/capabilities")).Content.ReadFromJsonAsync<JsonElement>();

        caps.GetProperty("accessModel").GetString().Should().Be("delegated");
        caps.GetProperty("supportedSources").EnumerateArray().Select(x => x.GetString()).Should().Equal("sharepoint", "onedrive_business");
        (await Preview(client, Candidates)).GetProperty("progress").GetProperty("includedCount").GetInt32().Should().Be(11);
        (await Preview(client, Archive)).GetProperty("requestId").GetString().Should().NotBeNullOrEmpty("a folder inside the permitted root is allowed");
        (await Preview(client, OneDrive)).GetProperty("requestId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Delegated_capabilities_expose_no_deployment_source_allow_list()
    {
        using var factory = CrossTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");

        var caps = await (await client.GetAsync("/api/bulk-scoring-requests/capabilities"))
            .Content.ReadFromJsonAsync<JsonElement>();
        caps.GetProperty("allowedHosts").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Another_organizational_tenant_uses_the_same_request_bound_authorization()
    {
        using var factory = CrossTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = (await Preview(client, Candidates)).GetProperty("requestId").GetString()!;

        var accepted = await (await Confirm(client, id)).Content.ReadFromJsonAsync<JsonElement>();

        accepted.GetProperty("accessModel").GetString().Should().Be("delegated");
        accepted.GetProperty("authorizationState").GetString().Should().Be("active");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BulkRequestAuthorizations.Count().Should().Be(1);
        db.BulkScoringRequests.Single(r => r.Id == id).ConnectionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_folder_identity_that_changes_after_preview_is_refused_when_confirming()
    {
        using var factory = CrossTenant();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = (await Preview(client, Candidates)).GetProperty("requestId").GetString()!;

        // The same draft now points at a folder whose ancestors no longer include the permitted root (moved elsewhere).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.BulkScoringRequests.SingleAsync(r => r.Id == id)).SourceFolderItemId = "sp-sibling";
            await db.SaveChangesAsync();
        }

        var confirm = await Confirm(client, id);

        confirm.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Code(confirm)).Should().BeOneOf(
            "sharepoint_access_denied",
            "validation_failed",
            "source_folder_invalid_or_unavailable");
        var draft = await (await client.GetAsync($"/api/bulk-scoring-requests/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        draft.GetProperty("status").GetString().Should().Be("draft");
    }

    // ---- helpers

    private static async Task ExpireAsync(BulkScoringRequestsEndpointsTests.BulkFactory factory, string requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.BulkScoringRequests.SingleAsync(r => r.Id == requestId);
        request.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static async Task SetConfigVersionAsync(BulkScoringRequestsEndpointsTests.BulkFactory factory, string version)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.JobConfigVersions.AnyAsync(config => config.Id == version))
            db.JobConfigVersions.Add(new JobConfigVersion
            {
                Id = version,
                JobId = "job-1",
                RubricApprovalStatus = "approved",
            });
        (await db.Jobs.SingleAsync(j => j.Id == "job-1")).CurrentConfigVersionId = version;
        await db.SaveChangesAsync();
    }

    // Stands in for a verified connection whose protected authorization cannot be transferred.
    private sealed class CannotStoreAuthorization : IRequestAuthorizationStore
    {
        public Task<BulkRequestAuthorization> TransferToRequestAsync(
            string requestId,
            string connectionId,
            string recruiterActorId,
            string jobId,
            CancellationToken cancellationToken = default) =>
            throw new AuthorizationRequiredException("Sign in again to continue.");

        public Task<string> AcquireGraphTokenAsync(string requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkLapsedAsync(string requestId, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DiscardAsync(string requestId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
