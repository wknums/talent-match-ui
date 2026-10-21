using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class BulkTerminalNotificationEndpointsTests
{
    [Fact]
    public async Task Typed_client_uses_the_narrow_item_name_free_notification_contract()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        var (requestId, ownerId, terminalAt) = await SeedTerminalRequestAsync(factory);
        var api = new TalentMatch.Web.Client.Services.ApiClient(owner);

        var notification = (await api.GetBulkScoringTerminalNotificationsAsync())
            .Should().ContainSingle().Which;

        using (new AssertionScope())
        {
            notification.RequestId.Should().Be(requestId);
            notification.RecipientActorId.Should().Be(ownerId);
            notification.TerminalAt.Should().Be(terminalAt);
            notification.Counts.Should().BeEquivalentTo(new
            {
                Completed = 3,
                Failed = 2,
                Skipped = 1,
                Cancelled = 4,
            });
            notification.GetType().GetProperties().Select(property => property.Name)
                .Should().BeEquivalentTo(
                    "RequestId",
                    "RecipientActorId",
                    "Outcome",
                    "Counts",
                    "TerminalAt",
                    "RequestDetailPath");
        }
    }

    [Fact]
    public async Task First_terminal_notification_is_submitter_only_frozen_item_name_free_and_links_to_authorized_detail()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        using var other = await factory.LoginAsync("recruiter2", "test123");
        using var anonymous = factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        var (requestId, ownerId, terminalAt) = await SeedTerminalRequestAsync(factory);

        var ownerResponse = await owner.GetAsync("/api/bulk-scoring-requests/notifications");
        ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ownerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var notification = payload.EnumerateArray().Should().ContainSingle().Which;

        using (new AssertionScope())
        {
            notification.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                "requestId",
                "recipientActorId",
                "outcome",
                "counts",
                "terminalAt",
                "requestDetailPath");
            notification.GetProperty("requestId").GetString().Should().Be(requestId);
            notification.GetProperty("recipientActorId").GetString().Should().Be(ownerId);
            notification.GetProperty("outcome").GetString().Should().Be("partial");
            notification.GetProperty("terminalAt").GetDateTime().Should().Be(terminalAt);
            notification.GetProperty("counts").EnumerateObject()
                .Select(property => property.Name)
                .Should().BeEquivalentTo("completed", "failed", "skipped", "cancelled");
            notification.GetProperty("counts").GetProperty("completed").GetInt32().Should().Be(3);
            notification.GetProperty("counts").GetProperty("failed").GetInt32().Should().Be(2);
            notification.GetProperty("counts").GetProperty("skipped").GetInt32().Should().Be(1);
            notification.GetProperty("counts").GetProperty("cancelled").GetInt32().Should().Be(4);
            AssertNoItemNameField(notification);
        }

        var detailPath = notification.GetProperty("requestDetailPath").GetString()!;
        Uri.TryCreate(detailPath, UriKind.Relative, out _).Should().BeTrue(
            "the server, not client input, supplies an application-relative path");
        detailPath.Should().Be($"/api/bulk-scoring-requests/{requestId}");
        (await owner.GetAsync(detailPath)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync(detailPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var otherNotifications = await other.GetAsync("/api/bulk-scoring-requests/notifications");
        otherNotifications.StatusCode.Should().Be(HttpStatusCode.OK);
        (await otherNotifications.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength()
            .Should().Be(0, "job access does not make another recruiter a notification recipient");
    }

    [Fact]
    public async Task Acknowledgment_is_owner_bound_retained_and_idempotent_without_recreating_notification()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        using var other = await factory.LoginAsync("recruiter2", "test123");
        var correlationId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        owner.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
        var (requestId, ownerId, _) = await SeedTerminalRequestAsync(factory);

        (await owner.PostAsync($"/api/bulk-scoring-requests/{requestId}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var first = await ReadAcknowledgmentAsync(factory, requestId);

        (await owner.PostAsync($"/api/bulk-scoring-requests/{requestId}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsync($"/api/bulk-scoring-requests/{requestId}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await other.PostAsync($"/api/bulk-scoring-requests/{requestId}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var retained = await ReadAcknowledgmentAsync(factory, requestId);
        using (new AssertionScope())
        {
            first.ActorId.Should().Be(ownerId);
            first.At.Should().NotBeNull();
            first.At!.Value.Kind.Should().Be(DateTimeKind.Utc);
            retained.Should().Be(first);
            retained.AuditRows.Should().Be(1);
            retained.AuditCorrelationId.Should().Be(correlationId);
            retained.AuditActorId.Should().Be(ownerId);
            retained.AuditOccurredAtKind.Should().Be(DateTimeKind.Utc);
        }

        var notifications = await owner.GetAsync("/api/bulk-scoring-requests/notifications");
        notifications.StatusCode.Should().Be(HttpStatusCode.OK);
        (await notifications.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Terminal_lifecycle_operations_append_every_required_safe_attributed_immutable_audit_row()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var owner = await factory.LoginAsync("recruiter", "test123");
        var correlationId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
        owner.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
        var (requestId, ownerId) = await SeedFinalizableRequestWithUserAuditAsync(
            factory,
            correlationId);

        BulkScoringRequest? firstFinalization;
        BulkScoringRequest? replayedFinalization;
        await using (var firstScope = factory.Services.CreateAsyncScope())
        {
            firstFinalization = await firstScope.ServiceProvider
                .GetRequiredService<IBulkScoringRepository>()
                .TryFinalizeAsync(
                    requestId,
                    new DateTime(2026, 10, 14, 19, 0, 0, DateTimeKind.Utc));
        }
        await using (var replayScope = factory.Services.CreateAsyncScope())
        {
            replayedFinalization = await replayScope.ServiceProvider
                .GetRequiredService<IBulkScoringRepository>()
                .TryFinalizeAsync(
                    requestId,
                    new DateTime(2026, 10, 14, 19, 0, 1, DateTimeKind.Utc));
        }

        firstFinalization.Should().NotBeNull();
        replayedFinalization.Should().BeNull("only the first terminal finalizer may create a notification");
        (await owner.PostAsync($"/api/bulk-scoring-requests/{requestId}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        List<BulkAuditEvent> rows;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            rows = await db.BulkAuditEvents.AsNoTracking()
                .Where(row => row.CorrelationId == correlationId)
                .OrderBy(row => row.OccurredAt)
                .ThenBy(row => row.EventType)
                .ToListAsync();
        }

        var required = new[]
        {
            BulkAuditEventTypes.BulkRequestConfirmed,
            BulkAuditEventTypes.BulkRequestCancellationRequested,
            BulkAuditEventTypes.BulkRequestRetryCreated,
            BulkAuditEventTypes.BulkRequestCountsReconciled,
            BulkAuditEventTypes.BulkRequestTerminalFinalized,
            BulkAuditEventTypes.BulkNotificationCreated,
            BulkAuditEventTypes.BulkNotificationAcknowledged,
        };
        var userActions = new HashSet<string>(StringComparer.Ordinal)
        {
            BulkAuditEventTypes.BulkRequestConfirmed,
            BulkAuditEventTypes.BulkRequestCancellationRequested,
            BulkAuditEventTypes.BulkRequestRetryCreated,
            BulkAuditEventTypes.BulkNotificationAcknowledged,
        };
        var backgroundActions = required.Where(type => !userActions.Contains(type)).ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
            row.Invoking(candidate => candidate.Validate()).Should().NotThrow();

        using (new AssertionScope())
        {
            rows.Select(row => row.EventType).Should().Contain(required);
            rows.Where(row => userActions.Contains(row.EventType)).Should().OnlyContain(row =>
                row.ActorType == BulkAuditActorType.User
                && row.ActorId == ownerId
                && row.InitiatedByActorId == null);
            rows.Where(row => backgroundActions.Contains(row.EventType)).Should().OnlyContain(row =>
                row.ActorType == BulkAuditActorType.Service
                && !string.IsNullOrWhiteSpace(row.ActorId)
                && row.InitiatedByActorId == ownerId);
            rows.Should().ContainSingle(row =>
                row.EventType == BulkAuditEventTypes.BulkRequestTerminalFinalized
                && row.ActorId == "bulk-request-finalization-service");
            rows.Should().OnlyContain(row =>
                row.CorrelationId == correlationId
                && row.OccurredAt.Kind == DateTimeKind.Utc
                && row.SchemaVersion > 0
                && row.SafeFactsJson.Contains("\"factVersion\":1", StringComparison.Ordinal));
            JsonSerializer.Serialize(rows).Should().NotContainAny(
                "Candidates",
                "cv-0.pdf",
                "contoso.sharepoint.com",
                "token",
                "verifier",
                "cache",
                "key",
                "fingerprint",
                "contentBase64");
            typeof(BulkAuditEvent).GetProperties()
                .Where(property => property.SetMethod is not null)
                .Should().OnlyContain(property =>
                    property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
                        .Contains(typeof(IsExternalInit)));
        }

        var retainedIds = rows.Where(row => required.Contains(row.EventType))
            .Select(row => row.AuditEventId)
            .ToHashSet(StringComparer.Ordinal);
        await using (var cleanupScope = factory.Services.CreateAsyncScope())
        {
            var db = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Jobs.Remove(await db.Jobs.SingleAsync(job => job.Id == "job-1"));
            await db.SaveChangesAsync();
        }
        await using (var verificationScope = factory.Services.CreateAsyncScope())
        {
            var db = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.BulkAuditEvents.AsNoTracking()
                .Where(row => retainedIds.Contains(row.AuditEventId))
                .Select(row => row.AuditEventId)
                .ToListAsync())
                .Should().BeEquivalentTo(retainedIds, "audit rows survive request and job cleanup");
        }
    }

    [Fact]
    public void SQLite_and_SQL_Server_models_and_scripts_have_identical_terminal_notification_and_audit_contracts()
    {
        using var sqlite = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        using var sqlServer = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BulkTerminalNotificationModelOnly;Trusted_Connection=True")
                .Options);

        static string[] Properties<T>(AppDbContext context) =>
            context.Model.FindEntityType(typeof(T))!.GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

        var requestContract = new[]
        {
            nameof(BulkScoringRequest.Outcome),
            nameof(BulkScoringRequest.CompletedCount),
            nameof(BulkScoringRequest.FailedCount),
            nameof(BulkScoringRequest.SkippedCount),
            nameof(BulkScoringRequest.CancelledCount),
            nameof(BulkScoringRequest.TerminalAt),
            nameof(BulkScoringRequest.AcknowledgedAt),
            nameof(BulkScoringRequest.AcknowledgedByActorId),
        };
        var auditContract = new[]
        {
            nameof(BulkAuditEvent.AuditEventId),
            nameof(BulkAuditEvent.EventType),
            nameof(BulkAuditEvent.CorrelationId),
            nameof(BulkAuditEvent.ActorType),
            nameof(BulkAuditEvent.ActorId),
            nameof(BulkAuditEvent.InitiatedByActorId),
            nameof(BulkAuditEvent.OccurredAt),
            nameof(BulkAuditEvent.SubjectType),
            nameof(BulkAuditEvent.SubjectId),
            nameof(BulkAuditEvent.SafeFactsJson),
            nameof(BulkAuditEvent.SchemaVersion),
            nameof(BulkAuditEvent.Boundary),
        };

        using (new AssertionScope())
        {
            Properties<BulkScoringRequest>(sqlite).Should().Contain(requestContract);
            Properties<BulkScoringRequest>(sqlServer).Should().Contain(requestContract);
            Properties<BulkScoringRequest>(sqlite).Should().Equal(Properties<BulkScoringRequest>(sqlServer));
            Properties<BulkAuditEvent>(sqlite).Should().BeEquivalentTo(auditContract);
            Properties<BulkAuditEvent>(sqlServer).Should().BeEquivalentTo(auditContract);

            var sqliteScript = sqlite.GetService<IMigrator>().GenerateScript();
            var sqlServerScript = sqlServer.GetService<IMigrator>().GenerateScript();
            foreach (var column in requestContract)
            {
                sqliteScript.Should().Contain(column);
                sqlServerScript.Should().Contain(column);
            }
            sqliteScript.Should().Contain("BulkAuditEvents");
            sqlServerScript.Should().Contain("BulkAuditEvents");
        }
    }

    private static async Task<(string RequestId, string OwnerId, DateTime TerminalAt)>
        SeedTerminalRequestAsync(BulkScoringRequestsEndpointsTests.BulkFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerId = await db.Users.Where(user => user.Username == "recruiter")
            .Select(user => user.Id)
            .SingleAsync();
        var terminalAt = new DateTime(2026, 10, 14, 18, 30, 0, DateTimeKind.Utc);
        var request = new BulkScoringRequest
        {
            Id = Guid.NewGuid().ToString(),
            JobId = "job-1",
            OwnerActorId = ownerId,
            Status = BulkRequestStatus.Active,
            SourceKind = BulkSourceKind.SharePoint,
            SourceTenantId = "fixture-tenant",
            SourceAccountBindingHash = "fixture-binding",
            SourceDriveId = "fixture-drive",
            SourceFolderItemId = "fixture-folder",
            SourceFolderName = "must-not-be-in-notification",
            SourceFolderPath = "/must-not-be-in-notification",
            SourceWebUrl = "https://contoso.sharepoint.com/sites/hr/must-not-be-in-notification",
            MaxFilesPerRequest = 30_000,
            MaxBytesPerFile = 8_388_608,
            MaxTotalBytes = 53_687_091_200,
            DiscoveryStepMaxFiles = 1_000,
            DiscoveryStepMaxEntries = 2_000,
            DiscoveryStepMaxSeconds = 30,
            IncludedItemCount = 10,
            IncludedBytes = 10_000,
            CreatedAt = terminalAt.AddMinutes(-10),
            CorrelationId = Guid.NewGuid().ToString(),
        };
        request.ApplyCounts(
            new Dictionary<BulkItemStatus, int>
            {
                [BulkItemStatus.Completed] = 3,
                [BulkItemStatus.Failed] = 2,
                [BulkItemStatus.Skipped] = 1,
                [BulkItemStatus.Cancelled] = 4,
            },
            totalEligibleBytes: 10_000);
        request.Complete(terminalAt);
        db.BulkScoringRequests.Add(request);
        await db.SaveChangesAsync();
        return (request.Id, ownerId, terminalAt);
    }

    private static async Task<(string RequestId, string OwnerId)>
        SeedFinalizableRequestWithUserAuditAsync(
            BulkScoringRequestsEndpointsTests.BulkFactory factory,
            string correlationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerId = await db.Users.Where(user => user.Username == "recruiter")
            .Select(user => user.Id)
            .SingleAsync();
        var now = new DateTime(2026, 10, 14, 18, 45, 0, DateTimeKind.Utc);
        var request = new BulkScoringRequest
        {
            Id = Guid.NewGuid().ToString(),
            JobId = "job-1",
            OwnerActorId = ownerId,
            Status = BulkRequestStatus.Active,
            SourceKind = BulkSourceKind.SharePoint,
            SourceTenantId = "fixture-tenant",
            SourceAccountBindingHash = "fixture-binding",
            SourceDriveId = "fixture-drive",
            SourceFolderItemId = "fixture-folder",
            SourceFolderName = "must-not-be-audited",
            SourceFolderPath = "/must-not-be-audited",
            SourceWebUrl = "https://contoso.sharepoint.com/sites/hr/must-not-be-audited",
            MaxFilesPerRequest = 30_000,
            MaxBytesPerFile = 8_388_608,
            MaxTotalBytes = 53_687_091_200,
            DiscoveryStepMaxFiles = 1_000,
            DiscoveryStepMaxEntries = 2_000,
            DiscoveryStepMaxSeconds = 30,
            IncludedItemCount = 10,
            IncludedBytes = 10_000,
            CreatedAt = now.AddMinutes(-10),
            CorrelationId = correlationId,
        };
        var statuses = new[]
        {
            BulkItemStatus.Completed,
            BulkItemStatus.Completed,
            BulkItemStatus.Completed,
            BulkItemStatus.Failed,
            BulkItemStatus.Failed,
            BulkItemStatus.Skipped,
            BulkItemStatus.Cancelled,
            BulkItemStatus.Cancelled,
            BulkItemStatus.Cancelled,
            BulkItemStatus.Cancelled,
        };
        var items = statuses.Select((status, ordinal) => new BulkScoringItem
        {
            Id = Guid.NewGuid().ToString(),
            RequestId = request.Id,
            Ordinal = ordinal,
            SourceDriveId = "fixture-drive",
            SourceDriveItemId = $"opaque-{ordinal}",
            SourceETag = $"opaque-version-{ordinal}",
            FileName = $"must-not-be-audited-{ordinal}.pdf",
            SourcePath = $"/must-not-be-audited/{ordinal}",
            MimeType = "application/pdf",
            SizeBytes = 1_000,
            Status = status,
            ReasonCode = status switch
            {
                BulkItemStatus.Failed => BulkReasonCode.TransientExhausted,
                BulkItemStatus.Skipped => BulkReasonCode.DuplicateInJob,
                BulkItemStatus.Cancelled => BulkReasonCode.CancelledByUser,
                _ => null,
            },
            TerminalAt = now,
            UpdatedAt = now,
        }).ToArray();
        request.ApplyItemCounts(items);

        db.BulkScoringRequests.Add(request);
        db.BulkScoringItems.AddRange(items);
        db.BulkAuditEvents.AddRange(
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkRequestConfirmed,
                ownerId,
                correlationId,
                now.AddMinutes(-9),
                "bulk_request",
                request.Id,
                new { state = "active" }),
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkRequestCancellationRequested,
                ownerId,
                correlationId,
                now.AddMinutes(-2),
                "bulk_request",
                request.Id,
                new { state = "cancellation_requested" }),
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkRequestRetryCreated,
                ownerId,
                correlationId,
                now.AddMinutes(-1),
                "bulk_request",
                "opaque-retry-request",
                new { state = "active", itemCount = 2 }));
        await db.SaveChangesAsync();
        return (request.Id, ownerId);
    }

    private static async Task<AcknowledgmentSnapshot> ReadAcknowledgmentAsync(
        BulkScoringRequestsEndpointsTests.BulkFactory factory,
        string requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.BulkScoringRequests.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == requestId);
        var audits = await db.BulkAuditEvents.AsNoTracking()
            .Where(row =>
                row.SubjectId == requestId
                && row.EventType == BulkAuditEventTypes.BulkNotificationAcknowledged)
            .ToListAsync();
        var audit = audits.SingleOrDefault();
        return new(
            request.AcknowledgedAt,
            request.AcknowledgedByActorId,
            audits.Count,
            audit?.CorrelationId,
            audit?.ActorId,
            audit?.OccurredAt.Kind);
    }

    private static void AssertNoItemNameField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                AssertNoItemNameField(child);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            new[]
            {
                "item",
                "items",
                "itemName",
                "itemNames",
                "fileName",
                "fileNames",
            }.Should().NotContain(property.Name);
            AssertNoItemNameField(property.Value);
        }
    }

    private sealed record AcknowledgmentSnapshot(
        DateTime? At,
        string? ActorId,
        int AuditRows,
        string? AuditCorrelationId,
        string? AuditActorId,
        DateTimeKind? AuditOccurredAtKind);
}
