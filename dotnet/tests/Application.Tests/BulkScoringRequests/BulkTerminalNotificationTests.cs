using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class BulkTerminalNotificationTests
{
    private static readonly DateTime TerminalAt =
        new(2026, 10, 14, 17, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task First_terminal_notification_is_submitter_only_and_has_a_dedicated_safe_shape()
    {
        var request = TerminalRequest();
        var repository = new Mock<IBulkScoringRepository>();
        repository.Setup(store => store.ListUnacknowledgedAsync(
                BulkHandlerKit.UserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([request]);

        var notifications = await new ListBulkScoringNotificationsHandler(
                BulkHandlerKit.Access(),
                repository.Object)
            .Handle(new ListBulkScoringNotificationsQuery(), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(
            notifications,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var notification = json.EnumerateArray().Should().ContainSingle().Which;

        using (new AssertionScope())
        {
            repository.Verify(store => store.ListUnacknowledgedAsync(
                BulkHandlerKit.UserId,
                It.IsAny<CancellationToken>()), Times.Once);
            notification.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                "requestId",
                "recipientActorId",
                "outcome",
                "counts",
                "terminalAt",
                "requestDetailPath");
            notification.GetProperty("recipientActorId").GetString().Should().Be(BulkHandlerKit.UserId);
            notification.GetProperty("outcome").GetString().Should().Be("partial");
            notification.GetProperty("terminalAt").GetDateTime().Should().Be(TerminalAt);
            notification.GetProperty("requestDetailPath").GetString().Should()
                .Be("/api/bulk-scoring-requests/req-1")
                .And.StartWith("/")
                .And.NotStartWith("//");

            var counts = notification.GetProperty("counts");
            counts.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                "completed", "failed", "skipped", "cancelled");
            counts.GetProperty("completed").GetInt32().Should().Be(3);
            counts.GetProperty("failed").GetInt32().Should().Be(2);
            counts.GetProperty("skipped").GetInt32().Should().Be(1);
            counts.GetProperty("cancelled").GetInt32().Should().Be(4);
            AssertNoItemNameField(notification);
        }
    }

    [Fact]
    public void First_terminal_transition_atomically_freezes_outcome_time_and_all_four_counts()
    {
        var request = TerminalRequest();
        var frozen = (
            request.Outcome,
            request.TerminalAt,
            request.CompletedCount,
            request.FailedCount,
            request.SkippedCount,
            request.CancelledCount);

        request.ApplyCounts(
            new Dictionary<BulkItemStatus, int> { [BulkItemStatus.Completed] = 10 },
            totalEligibleBytes: 10_000);

        (request.Outcome,
            request.TerminalAt,
            request.CompletedCount,
            request.FailedCount,
            request.SkippedCount,
            request.CancelledCount).Should().Be(
            frozen,
            "the first terminal transaction is the immutable source for notification outcome and counts");
    }

    [Fact]
    public async Task Acknowledgment_is_retained_once_and_every_replay_is_an_idempotent_no_op()
    {
        var request = TerminalRequest();
        var persisted = new List<BulkAuditEvent>();
        var repository = new Mock<IBulkScoringRepository>();
        repository.Setup(store => store.GetAsync("req-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        repository.Setup(store => store.AcknowledgeWithAuditAsync(
                "req-1",
                It.IsAny<DateTime>(),
                It.IsAny<BulkAuditEvent>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, DateTime, BulkAuditEvent, CancellationToken>((_, at, audit, _) =>
            {
                if (request.TryAcknowledge(audit.ActorId, at))
                    persisted.Add(audit);
            })
            .Returns(Task.CompletedTask);
        var handler = new AcknowledgeBulkScoringRequestHandler(
            BulkHandlerKit.Access(),
            repository.Object,
            BulkHandlerKit.Clock);
        var command = new AcknowledgeBulkScoringRequestCommand(
            "req-1",
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        await handler.Handle(command, CancellationToken.None);
        var first = (request.AcknowledgedAt, request.AcknowledgedByActorId);
        await handler.Handle(command, CancellationToken.None);
        await handler.Handle(command, CancellationToken.None);

        using (new AssertionScope())
        {
            first.Should().Be((BulkHandlerKit.Clock.GetUtcNow().UtcDateTime, BulkHandlerKit.UserId));
            (request.AcknowledgedAt, request.AcknowledgedByActorId).Should().Be(first);
            persisted.Should().ContainSingle();
            persisted.Single().EventType.Should().Be(BulkAuditEventTypes.BulkNotificationAcknowledged);
            persisted.Single().ActorType.Should().Be(BulkAuditActorType.User);
            persisted.Single().ActorId.Should().Be(BulkHandlerKit.UserId);
            persisted.Single().CorrelationId.Should().Be(command.CorrelationId);
            persisted.Single().OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        }
    }

    [Fact]
    public void Required_terminal_lifecycle_audit_envelopes_are_closed_immutable_UTC_and_payload_safe()
    {
        var correlationId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        var now = new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);
        BulkAuditEvent[] rows =
        [
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkRequestConfirmed,
                BulkHandlerKit.UserId,
                correlationId,
                now,
                "bulk_request",
                "req-1",
                new { state = "active" }),
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkRequestCancellationRequested,
                BulkHandlerKit.UserId,
                correlationId,
                now,
                "bulk_request",
                "req-1",
                new { state = "cancellation_requested" }),
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkRequestRetryCreated,
                BulkHandlerKit.UserId,
                correlationId,
                now,
                "bulk_request",
                "retry-1",
                new { state = "active", itemCount = 2 }),
            BulkAuditWriter.Service(
                BulkAuditEventTypes.BulkRequestCountsReconciled,
                "bulk-count-reconciliation-service",
                BulkHandlerKit.UserId,
                correlationId,
                now,
                "bulk_request",
                "req-1",
                new { state = "reconciled", completedCount = 3, failedCount = 2, skippedCount = 1, cancelledCount = 4 }),
            BulkAuditWriter.Service(
                BulkAuditEventTypes.BulkRequestTerminalFinalized,
                "bulk-request-finalization-service",
                BulkHandlerKit.UserId,
                correlationId,
                now,
                "bulk_request",
                "req-1",
                new { state = "completed", outcome = "partial", completedCount = 3, failedCount = 2, skippedCount = 1, cancelledCount = 4 }),
            BulkAuditWriter.Service(
                BulkAuditEventTypes.BulkNotificationCreated,
                "bulk-notification-service",
                BulkHandlerKit.UserId,
                correlationId,
                now,
                "bulk_request",
                "req-1",
                new { state = "created", outcome = "partial" }),
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkNotificationAcknowledged,
                BulkHandlerKit.UserId,
                correlationId,
                now,
                "bulk_request",
                "req-1",
                new { state = "acknowledged" }),
        ];

        foreach (var row in rows)
            row.Invoking(candidate => candidate.Validate()).Should().NotThrow();

        using (new AssertionScope())
        {
            rows.Select(row => row.EventType).Should().Equal(
                BulkAuditEventTypes.BulkRequestConfirmed,
                BulkAuditEventTypes.BulkRequestCancellationRequested,
                BulkAuditEventTypes.BulkRequestRetryCreated,
                BulkAuditEventTypes.BulkRequestCountsReconciled,
                BulkAuditEventTypes.BulkRequestTerminalFinalized,
                BulkAuditEventTypes.BulkNotificationCreated,
                BulkAuditEventTypes.BulkNotificationAcknowledged);
            rows.Take(3).Append(rows[^1]).Should().OnlyContain(row =>
                row.ActorType == BulkAuditActorType.User
                && row.ActorId == BulkHandlerKit.UserId
                && row.InitiatedByActorId == null);
            rows.Skip(3).Take(3).Should().OnlyContain(row =>
                row.ActorType == BulkAuditActorType.Service
                && !string.IsNullOrWhiteSpace(row.ActorId)
                && row.InitiatedByActorId == BulkHandlerKit.UserId);
            rows.Should().OnlyContain(row =>
                row.CorrelationId == correlationId
                && row.OccurredAt.Kind == DateTimeKind.Utc
                && row.SchemaVersion > 0
                && row.SafeFactsJson.Contains("\"factVersion\":1", StringComparison.Ordinal));
            JsonSerializer.Serialize(rows).Should().NotContainAny(
                "Candidates",
                "cv.pdf",
                "contoso.sharepoint.com",
                "token",
                "verifier",
                "fingerprint",
                "contentBase64");
            typeof(BulkAuditEvent).GetProperties()
                .Where(property => property.SetMethod is not null)
                .Should().OnlyContain(property =>
                    property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
                        .Contains(typeof(IsExternalInit)));
        }
    }

    private static BulkScoringRequest TerminalRequest()
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);
        request.ApplyCounts(
            new Dictionary<BulkItemStatus, int>
            {
                [BulkItemStatus.Completed] = 3,
                [BulkItemStatus.Failed] = 2,
                [BulkItemStatus.Skipped] = 1,
                [BulkItemStatus.Cancelled] = 4,
            },
            totalEligibleBytes: 10_000);
        request.Complete(TerminalAt);
        return request;
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
}
