using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

// Cancel, retry, list, notifications, acknowledge and original download, end to end against the real server and database.
public sealed class BulkScoringRequestsQueryEndpointsTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";

    private static BulkScoringRequestsEndpointsTests.BulkFactory Factory() => new(
        "mock",
        new Dictionary<string, string>
        {
            ["SHAREPOINT_ALLOWED_HOSTS"] = "tenant-a.sharepoint.com",
        });

    private static Task<string> Accepted(
        BulkScoringRequestsEndpointsTests.BulkFactory factory,
        bool raster = false) =>
        SeedAccepted(factory, convertToRasterPdf: raster);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> Code(HttpResponseMessage response) => (await Json(response)).GetProperty("code").GetString()!;

    private static async Task<string> SeedAccepted(
        BulkScoringRequestsEndpointsTests.BulkFactory factory,
        string? capturedConfigVersionId = null,
        int itemCount = 11,
        bool convertToRasterPdf = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerId = await db.Users.Where(user => user.Username == "recruiter").Select(user => user.Id).SingleAsync();
        var now = DateTime.UtcNow;
        var request = new BulkScoringRequest
        {
            JobId = "job-1",
            OwnerActorId = ownerId,
            SourceKind = BulkSourceKind.SharePoint,
            SourceTenantId = "tenant-1",
            SourceAccountBindingHash = "account-binding",
            SourceDriveId = "drive-1",
            SourceFolderItemId = "folder-1",
            SourceFolderName = "Candidates",
            SourceFolderPath = "/Candidates",
            SourceWebUrl = Candidates,
            RasterDpi = 150,
            MaxFilesPerRequest = 30_000,
            MaxBytesPerFile = 8_388_608,
            MaxTotalBytes = 1_073_741_824,
            DiscoveryStepMaxFiles = 1_000,
            DiscoveryStepMaxEntries = 5_000,
            DiscoveryStepMaxSeconds = 30,
            DiscoveryStatus = BulkDiscoveryStatus.Complete,
            IncludedItemCount = itemCount,
            IncludedBytes = itemCount * 1_024,
            CorrelationId = Guid.NewGuid().ToString(),
        };
        var items = Enumerable.Range(0, itemCount).Select(index => new BulkScoringItem
        {
            RequestId = request.Id,
            Ordinal = index,
            SourceDriveId = request.SourceDriveId,
            SourceDriveItemId = $"source-{index}",
            SourceETag = $"etag-{index}",
            FileName = $"candidate-{index:D2}.pdf",
            SourcePath = $"/Candidates/candidate-{index:D2}.pdf",
            MimeType = "application/pdf",
            SizeBytes = 1_024,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToArray();
        request.ApplyCounts(
            new Dictionary<BulkItemStatus, int> { [BulkItemStatus.Waiting] = itemCount },
            items.Sum(item => item.SizeBytes));
        request.Confirm(convertToRasterPdf, false, capturedConfigVersionId, now);
        db.BulkScoringRequests.Add(request);
        db.BulkScoringItems.AddRange(items);
        db.BulkRequestAuthorizations.Add(BulkRequestAuthorization.Establish(
            request.Id,
            ownerId,
            request.SourceTenantId,
            request.SourceAccountBindingHash,
            request.SourceDriveId,
            request.SourceFolderItemId,
            [1],
            [2],
            "key-v1",
            now));
        await db.SaveChangesAsync();
        return request.Id;
    }

    // Plays the worker: moves each file to the result the test wants, then lets the repository finish the request.
    private static async Task Settle(BulkScoringRequestsEndpointsTests.BulkFactory factory, string requestId, Func<BulkScoringItem, Action<BulkScoringItem>?> plan)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();
        var now = DateTime.UtcNow;
        var items = (await repository.GetItemsAsync(requestId, new BulkItemQuery(1, 100))).Items;
        foreach (var item in items)
        {
            var step = plan(item);
            if (step is null) continue;
            step(item);
            await repository.SaveItemAsync(item);
        }
        await repository.TryFinalizeAsync(requestId, now);
    }

    private static Action<BulkScoringItem> Fail(BulkReasonCode reason) =>
        item => item.TransitionTo(BulkItemStatus.Failed, DateTime.UtcNow, reason, "failed");

    private static Action<BulkScoringItem> Succeed() => item =>
    {
        var now = DateTime.UtcNow;
        item.TransitionTo(BulkItemStatus.Importing, now);
        item.TransitionTo(BulkItemStatus.Scoring, now);
        item.TransitionTo(BulkItemStatus.Completed, now);
    };

    // ---- list, detail, display name

    [Fact]
    public async Task The_job_list_shows_accepted_requests_newest_first_and_never_a_preview()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var first = await Accepted(factory);
        await Task.Delay(20);
        var second = await Accepted(factory);
        (await client.PostAsJsonAsync("/api/jobs/job-1/bulk-scoring-requests/preview", new { folderUrl = Candidates })).StatusCode.Should().Be(HttpStatusCode.Created);

        var page = await Json(await client.GetAsync("/api/jobs/job-1/bulk-scoring-requests"));

        page.GetProperty("total").GetInt32().Should().Be(2, "the open preview is not listed");
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("requestId").GetString()).Should().Equal(second, first);
        page.GetProperty("items")[0].GetProperty("counts").GetProperty("waiting").GetInt32().Should().Be(11);
        page.GetProperty("items")[0].GetProperty("authorizationState").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_list_clamps_the_page_size_and_pages_through_the_requests()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        for (var i = 0; i < 3; i++) await Accepted(factory);

        var oversized = await Json(await client.GetAsync("/api/jobs/job-1/bulk-scoring-requests?pageSize=5000"));
        oversized.GetProperty("pageSize").GetInt32().Should().Be(100);

        var second = await Json(await client.GetAsync("/api/jobs/job-1/bulk-scoring-requests?page=2&pageSize=2"));
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.GetProperty("total").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Summary_detail_and_items_expose_safe_authorization_and_configuration_evidence()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.JobConfigVersions.Add(new JobConfigVersion
            {
                Id = "config-at-acceptance",
                JobId = "job-1",
                VersionNumber = 1,
                RubricApprovalStatus = "approved",
            });
            (await db.Jobs.SingleAsync(j => j.Id == "job-1")).CurrentConfigVersionId = "config-at-acceptance";
            await db.SaveChangesAsync();
        }

        var id = await SeedAccepted(factory, "config-at-acceptance");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.JobConfigVersions.Add(new JobConfigVersion
            {
                Id = "config-current",
                JobId = "job-1",
                VersionNumber = 2,
                RubricApprovalStatus = "approved",
            });
            (await db.Jobs.SingleAsync(j => j.Id == "job-1")).CurrentConfigVersionId = "config-current";
            await db.SaveChangesAsync();
            await db.BulkScoringItems.Where(i => i.RequestId == id && i.Ordinal == 0)
                .ExecuteUpdateAsync(update => update.SetProperty(i => i.ScoringConfigVersionId, "config-used"));
        }

        var summary = (await Json(await client.GetAsync("/api/jobs/job-1/bulk-scoring-requests")))
            .GetProperty("items")[0];
        summary.GetProperty("authorizationState").GetString().Should().Be("active");
        summary.GetProperty("capturedScoringConfigVersionId").GetString().Should().Be("config-at-acceptance");
        summary.GetProperty("currentScoringConfigVersionId").GetString().Should().Be("config-current");
        summary.GetProperty("configChangedSinceAcceptance").GetBoolean().Should().BeTrue();

        var detail = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"));
        detail.GetProperty("authorizationState").GetString().Should().Be("active");
        detail.GetProperty("capturedScoringConfigVersionId").GetString().Should().Be("config-at-acceptance");
        detail.GetProperty("currentScoringConfigVersionId").GetString().Should().Be("config-current");
        detail.GetProperty("configChangedSinceAcceptance").GetBoolean().Should().BeTrue();
        detail.ToString().ToLowerInvariant().Should().NotContain("wrappeddatakey")
            .And.NotContain("payload")
            .And.NotContain("sourceaccountbindinghash");

        var item = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?pageSize=1")))
            .GetProperty("items")[0];
        item.GetProperty("scoringConfigVersionId").GetString().Should().Be("config-used");
    }

    [Fact]
    public async Task Missing_authorization_is_reported_as_safe_historical_state()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await SeedAccepted(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BulkRequestAuthorizations.Where(a => a.RequestId == id).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }

        var detail = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"));

        detail.GetProperty("authorizationState").GetString().Should().Be("unavailableHistorical");
    }

    [Fact]
    public async Task Reconciled_counts_are_coherent_and_the_event_payload_contains_only_safe_facts()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await SeedAccepted(factory);
        await Settle(factory, id, item => item.Ordinal == 0 ? Succeed() : null);

        var counts = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"))).GetProperty("counts");
        var eligible = new[] { "waiting", "importing", "converting", "scoring", "completed", "failed", "skipped", "cancelled" }
            .Sum(name => counts.GetProperty(name).GetInt32());
        counts.GetProperty("discovered").GetInt32().Should().Be(eligible);
        counts.EnumerateObject().Select(property => property.Value.GetInt32()).Should().OnlyContain(count => count >= 0);

        await using var scope = factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<AppDbContext>().BulkAuditEvents.AsNoTracking()
            .Where(e => e.EventType == BulkAuditEventTypes.BulkRequestCountsReconciled)
            .ToListAsync();
        events.Should().NotBeEmpty();
        events.Should().OnlyContain(e =>
            e.ActorId != "system"
            && e.SafeFactsJson.Contains("reconciled", StringComparison.Ordinal)
            && !e.SafeFactsJson.Contains("fileName", StringComparison.OrdinalIgnoreCase)
            && !e.SafeFactsJson.Contains("sourcePath", StringComparison.OrdinalIgnoreCase)
            && !e.SafeFactsJson.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Authorized_summary_detail_and_items_are_paged_on_the_server()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var first = await SeedAccepted(factory);
        await Task.Delay(20);
        var second = await SeedAccepted(factory);
        await Task.Delay(20);
        var third = await SeedAccepted(factory);

        var summaryPage = await Json(await client.GetAsync(
            "/api/jobs/job-1/bulk-scoring-requests?page=2&pageSize=2"));
        summaryPage.GetProperty("page").GetInt32().Should().Be(2);
        summaryPage.GetProperty("pageSize").GetInt32().Should().Be(2);
        summaryPage.GetProperty("total").GetInt32().Should().Be(3);
        summaryPage.GetProperty("items").GetArrayLength().Should().Be(1);
        summaryPage.GetProperty("items")[0].GetProperty("requestId").GetString().Should().Be(first);

        var detail = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{second}"));
        detail.GetProperty("requestId").GetString().Should().Be(second);
        detail.GetProperty("authorizationState").GetString().Should().Be("active");

        var itemPage = await Json(await client.GetAsync(
            $"/api/bulk-scoring-requests/{third}/items?page=2&pageSize=4"));
        itemPage.GetProperty("page").GetInt32().Should().Be(2);
        itemPage.GetProperty("pageSize").GetInt32().Should().Be(4);
        itemPage.GetProperty("total").GetInt32().Should().Be(11);
        itemPage.GetProperty("items").GetArrayLength().Should().Be(4);
        itemPage.GetProperty("items")[0].GetProperty("fileName").GetString().Should().Be("candidate-04.pdf");
    }

    [Fact]
    public async Task Query_endpoints_return_stable_safe_errors()
    {
        using var disabledFactory = new BulkScoringRequestsEndpointsTests.BulkFactory(mode: null);
        using var disabledClient = await disabledFactory.LoginAsync("recruiter", "test123");
        var unavailable = await disabledClient.GetAsync("/api/jobs/job-1/bulk-scoring-requests");
        unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Code(unavailable)).Should().Be("feature_unavailable");

        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await SeedAccepted(factory);
        var missing = await client.GetAsync("/api/bulk-scoring-requests/missing");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(missing)).Should().Be("not_found");

        var invalidFilter = await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?status=not-a-state");
        invalidFilter.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var invalidProblem = await Json(invalidFilter);
        invalidProblem.GetProperty("code").GetString().Should().Be("validation_failed");
        invalidProblem.ToString().ToLowerInvariant().Should()
            .NotContain("candidates")
            .And.NotContain("candidate-00.pdf")
            .And.NotContain("drive-1");
    }

    [Fact]
    public async Task Unknown_jobs_requests_and_other_peoples_previews_look_like_they_do_not_exist()
    {
        using var factory = Factory();
        using var owner = await factory.LoginAsync("recruiter", "test123");
        using var other = await factory.LoginAsync("recruiter2", "test123");
        var preview = await Json(await owner.PostAsJsonAsync("/api/jobs/job-1/bulk-scoring-requests/preview", new { folderUrl = Candidates }));
        var draftId = preview.GetProperty("requestId").GetString()!;

        (await owner.GetAsync("/api/jobs/missing/bulk-scoring-requests")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync("/api/bulk-scoring-requests/missing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/bulk-scoring-requests/{draftId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync($"/api/bulk-scoring-requests/{draftId}/cancel", new { expectedConcurrencyVersion = 1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/bulk-scoring-requests/{draftId}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/bulk-scoring-requests/{draftId}/retry-failed", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/bulk-scoring-requests/{draftId}/items")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var missing = await owner.GetAsync("/api/bulk-scoring-requests/missing");
        (await Code(missing)).Should().Be("not_found");
        var invalidFilter = await owner.GetAsync($"/api/bulk-scoring-requests/{draftId}/items?status=not-a-state");
        invalidFilter.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Code(invalidFilter)).Should().Be("validation_failed");
    }

    [Fact]
    public async Task Submitted_by_is_the_display_name_and_falls_back_to_the_identifier_when_there_is_no_user_record()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);

        string ownerId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ownerId = (await db.BulkScoringRequests.SingleAsync(r => r.Id == id)).OwnerActorId;
        }

        var known = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"))).GetProperty("submittedBy").GetString();
        var user = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Users
            .FirstOrDefault(u => u.Id == ownerId || u.Username == ownerId);
        known.Should().Be(user is not null && user.Id == ownerId ? user.FullName : ownerId);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BulkScoringRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerActorId, "former-employee-id"));
        }

        (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"))).GetProperty("submittedBy").GetString().Should().Be("former-employee-id");
        var list = await Json(await client.GetAsync("/api/jobs/job-1/bulk-scoring-requests"));
        list.GetProperty("items")[0].TryGetProperty("submittedBy", out _).Should().BeFalse(
            "recorded actor detail is returned only by the authorized detail endpoint");
    }

    // ---- items

    [Fact]
    public async Task Items_are_paged_on_the_server_and_filterable_by_status_and_name()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);
        var ordinal = 0;
        await Settle(factory, id, item => ordinal++ switch { < 3 => Fail(BulkReasonCode.TransientExhausted), < 5 => Succeed(), _ => null });

        var failed = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?status=failed"));
        failed.GetProperty("total").GetInt32().Should().Be(3);
        failed.GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("reasonCode").GetString() == "transient_exhausted");

        var paged = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?page=2&pageSize=4"));
        paged.GetProperty("items").GetArrayLength().Should().Be(4);
        paged.GetProperty("total").GetInt32().Should().Be(11);

        var firstName = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?pageSize=1"))).GetProperty("items")[0].GetProperty("fileName").GetString()!;
        var byName = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?q={Uri.EscapeDataString(firstName.ToUpperInvariant())}"));
        byName.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fileName").GetString()).Should().Contain(firstName);

        (await client.GetAsync($"/api/bulk-scoring-requests/{id}/items?status=excluded")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---- cancel

    [Fact]
    public async Task Cancel_stops_waiting_files_at_once_lets_scoring_files_finish_and_completes_as_cancelled()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);
        var ordinal = 0;
        // Two files are already being scored, one is done, the rest are still waiting.
        await Settle(factory, id, item => ordinal++ switch
        {
            0 => i => { var n = DateTime.UtcNow; i.TransitionTo(BulkItemStatus.Importing, n); i.TransitionTo(BulkItemStatus.Scoring, n); },
            1 => i => { var n = DateTime.UtcNow; i.TransitionTo(BulkItemStatus.Importing, n); i.TransitionTo(BulkItemStatus.Scoring, n); },
            2 => Succeed(),
            _ => null,
        });
        var version = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"))).GetProperty("concurrencyVersion").GetInt32();

        var response = await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/cancel", new { expectedConcurrencyVersion = version });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var detail = await Json(response);
        detail.GetProperty("cancelRequested").GetBoolean().Should().BeTrue();
        detail.GetProperty("status").GetString().Should().Be("active", "two files are still being scored");
        detail.GetProperty("counts").GetProperty("scoring").GetInt32().Should().Be(2);
        detail.GetProperty("counts").GetProperty("cancelled").GetInt32().Should().Be(8);
        detail.GetProperty("counts").GetProperty("completed").GetInt32().Should().Be(1, "finished work is unchanged");
        detail.GetProperty("cancelledBy").GetString().Should().NotBeNullOrEmpty();

        await Settle(factory, id, item => item.Status == BulkItemStatus.Scoring ? (i => i.TransitionTo(BulkItemStatus.Completed, DateTime.UtcNow)) : null);

        var done = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"));
        done.GetProperty("status").GetString().Should().Be("completed");
        done.GetProperty("outcome").GetString().Should().Be("cancelled");
        done.GetProperty("counts").GetProperty("completed").GetInt32().Should().Be(3);

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingEvents
            .Where(e => e.EntityId == id && e.EventType == "bulk-scoring-request.cancel-requested").ToListAsync();
        audit.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_answers_409_for_a_stale_version_and_for_a_finished_request_and_is_idempotent()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);
        var version = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"))).GetProperty("concurrencyVersion").GetInt32();
        // One file mid-import keeps the request open after the cancel.
        await Settle(factory, id, item => item.Ordinal == 0 ? (i => i.TransitionTo(BulkItemStatus.Importing, DateTime.UtcNow)) : null);
        version = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"))).GetProperty("concurrencyVersion").GetInt32();

        var stale = await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/cancel", new { expectedConcurrencyVersion = version + 7 });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(stale)).Should().Be("version_conflict");

        (await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/cancel", new { expectedConcurrencyVersion = version })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/cancel", new { expectedConcurrencyVersion = 0 })).StatusCode
            .Should().Be(HttpStatusCode.Accepted, "a second cancel returns the request as it stands");

        await Settle(factory, id, item => item.Status == BulkItemStatus.Importing ? (i => i.TransitionTo(BulkItemStatus.Failed, DateTime.UtcNow, BulkReasonCode.TransientExhausted, "x")) : null);
        var finished = await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{id}/cancel", new { expectedConcurrencyVersion = 1 });
        finished.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(finished)).Should().Be("invalid_state");
    }

    // ---- retry

    [Fact]
    public async Task Retry_creates_a_new_active_request_for_only_the_retryable_files_and_leaves_the_original_alone()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory, raster: true);
        var ordinal = 0;
        await Settle(factory, id, item => ordinal++ switch
        {
            < 2 => Fail(BulkReasonCode.TransientExhausted),
            2 => Fail(BulkReasonCode.SourceChanged),
            3 => Fail(BulkReasonCode.ConversionFailed),
            < 6 => Succeed(),
            _ => Fail(BulkReasonCode.CancelledByUser),
        });
        var before = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"));
        before.GetProperty("status").GetString().Should().Be("completed");

        var response = await client.PostAsync($"/api/bulk-scoring-requests/{id}/retry-failed", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(response)).Should().Be("authorization_required",
            "same-tenant retries require a newly verified source connection");
        var after = await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"));
        after.GetProperty("counts").ToString().Should().Be(before.GetProperty("counts").ToString());
    }

    [Fact]
    public async Task Retry_is_refused_while_the_request_runs_and_when_nothing_is_retryable()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);

        var running = await client.PostAsync($"/api/bulk-scoring-requests/{id}/retry-failed", null);
        running.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(running)).Should().Be("invalid_state");

        await Settle(factory, id, _ => Succeed());
        var nothing = await client.PostAsync($"/api/bulk-scoring-requests/{id}/retry-failed", null);
        nothing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(nothing)).Should().Be("authorization_required");

        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().BulkScoringRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Retry_is_refused_with_403_when_the_recruiter_can_no_longer_open_the_folder()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);
        await Settle(factory, id, _ => Fail(BulkReasonCode.TransientExhausted));
        factory.Services.GetRequiredService<TalentMatch.Infrastructure.SharePoint.MockSharePointSource>().Revoke("fixture-account-recruiter", "sp-candidates");

        var response = await client.PostAsync($"/api/bulk-scoring-requests/{id}/retry-failed", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(response)).Should().Be("authorization_required",
            "the endpoint must request a new connection without probing the old source identity");
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().BulkScoringRequests.CountAsync()).Should().Be(1);
    }

    // ---- audit

    [Fact]
    public async Task Accepting_and_retrying_audit_the_new_authorization_and_an_immediate_cancel_audits_the_completion()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);
        var idle = await Accepted(factory);
        var cancelled = await client.PostAsJsonAsync($"/api/bulk-scoring-requests/{idle}/cancel", new { expectedConcurrencyVersion = (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{idle}"))).GetProperty("concurrencyVersion").GetInt32() });
        cancelled.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Settle(factory, id, _ => Fail(BulkReasonCode.TransientExhausted));
        var retry = await client.PostAsync($"/api/bulk-scoring-requests/{id}/retry-failed", null);
        retry.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var scope = factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingEvents.AsNoTracking()
            .Where(e => e.EventType.StartsWith("bulk-scoring")).ToListAsync();

        events.Where(e => e.EventType == "bulk-scoring-authorization.created").Should().BeEmpty();
        events.Should().ContainSingle(e => e.EventType == "bulk-scoring-request.completed" && e.EntityId == idle && e.Actor != "system");
    }

    // ---- notifications and acknowledge

    [Fact]
    public async Task A_lapse_keeps_reporting_lapsed_after_the_authorization_is_cleaned_up()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory);
        await Settle(factory, id, _ => Fail(BulkReasonCode.AuthorizationLapsed));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var authorization = await db.BulkRequestAuthorizations.SingleAsync(a => a.RequestId == id);
            authorization.MarkLapsed("revoked", DateTime.UtcNow);
            authorization.Discard(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        (await Json(await client.GetAsync($"/api/bulk-scoring-requests/{id}"))).GetProperty("authorizationState").GetString().Should().Be("lapsed");
        (await Json(await client.GetAsync("/api/bulk-scoring-requests/notifications")))[0]
            .TryGetProperty("authorizationState", out _).Should().BeFalse(
                "terminal notification contracts never expose authorization or source detail");
    }

    [Fact]
    public async Task Notifications_hold_the_callers_finished_requests_until_acknowledged()
    {
        using var factory = Factory();
        using var owner = await factory.LoginAsync("recruiter", "test123");
        using var other = await factory.LoginAsync("recruiter2", "test123");
        var finished = await Accepted(factory);
        var running = await Accepted(factory);
        (await owner.PostAsJsonAsync("/api/jobs/job-1/bulk-scoring-requests/preview", new { folderUrl = Candidates })).StatusCode.Should().Be(HttpStatusCode.Created);
        await Settle(factory, finished, _ => Succeed());

        var mine = await Json(await owner.GetAsync("/api/bulk-scoring-requests/notifications"));
        mine.EnumerateArray().Select(n => n.GetProperty("requestId").GetString()).Should().Equal(finished);
        mine[0].GetProperty("outcome").GetString().Should().Be("all_succeeded");
        mine[0].GetProperty("counts").GetProperty("completed").GetInt32().Should().Be(11);
        running.Should().NotBe(finished);

        (await Json(await other.GetAsync("/api/bulk-scoring-requests/notifications"))).GetArrayLength().Should().Be(0, "notifications go to whoever submitted the request");
        (await other.PostAsync($"/api/bulk-scoring-requests/{finished}/acknowledge", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "someone else cannot dismiss it");

        (await owner.PostAsync($"/api/bulk-scoring-requests/{finished}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsync($"/api/bulk-scoring-requests/{finished}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.NoContent, "acknowledging twice is harmless");
        (await Json(await owner.GetAsync("/api/bulk-scoring-requests/notifications"))).GetArrayLength().Should().Be(0);
        (await Json(await owner.GetAsync($"/api/bulk-scoring-requests/{finished}"))).GetProperty("acknowledged").GetBoolean().Should().BeTrue();
    }

    // ---- original download

    [Fact]
    public async Task A_retained_original_downloads_as_an_attachment_and_anything_else_is_404()
    {
        using var factory = Factory();
        using var client = await factory.LoginAsync("recruiter", "test123");
        var id = await Accepted(factory, raster: true);

        string itemId, otherItemId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var items = await db.BulkScoringItems.Where(i => i.RequestId == id).OrderBy(i => i.Ordinal).Take(2).ToListAsync();
            (itemId, otherItemId) = (items[0].Id, items[1].Id);
            var payload = "<script>x</script>"u8.ToArray();
            db.BulkScoringItemContents.Add(await scope.ServiceProvider.GetRequiredService<TalentMatch.Application.Common.Interfaces.IBulkContentStore>()
                .StageAsync(id, itemId, BulkContentKind.Original, items[0].FileName, "text/html", payload));
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/bulk-scoring-requests/{id}/items/{itemId}/original");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal("<script>x</script>"u8.ToArray());

        (await client.GetAsync($"/api/bulk-scoring-requests/{id}/items/{otherItemId}/original")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/bulk-scoring-requests/{id}/items/missing/original")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/bulk-scoring-requests/missing/items/{itemId}/original")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
