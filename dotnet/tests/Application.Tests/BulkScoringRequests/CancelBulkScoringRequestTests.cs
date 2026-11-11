using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class CancelBulkScoringRequestTests
{
    private const string CorrelationId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private readonly Mock<IBulkScoringRepository> _repository = new();
    private readonly Mock<IProcessingEventRepository> _events = new();
    private readonly List<ProcessingEvent> _audit = [];

    public CancelBulkScoringRequestTests()
    {
        _events.Setup(e => e.AddAsync(It.IsAny<ProcessingEvent>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingEvent, CancellationToken>((evt, _) => _audit.Add(evt))
            .Returns(Task.CompletedTask);
    }

    private CancelBulkScoringRequestHandler Handler(Mock<IJobRepository>? jobs = null) =>
        new(BulkHandlerKit.Access(), _repository.Object, _events.Object, (jobs ?? BulkHandlerKit.Jobs()).Object, BulkHandlerKit.Clock);

    private static CancelBulkScoringRequestCommand Command(int version = 1) => new("req-1", version, CorrelationId);

    [Fact]
    public async Task Cancelling_an_active_request_records_who_and_when_and_writes_an_audit_entry()
    {
        var active = BulkHandlerKit.Request(BulkRequestStatus.Active);
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(active);
        BulkAuditEvent? durableAudit = null;

        // The repository applies the cancel: waiting files stop now, two files are still being scored.
        var cancelled = BulkHandlerKit.Request(BulkRequestStatus.Active);
        cancelled.RequestCancel(BulkHandlerKit.UserId, BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        cancelled.ApplyCounts(new Dictionary<BulkItemStatus, int>
        {
            [BulkItemStatus.Cancelled] = 7, [BulkItemStatus.Scoring] = 2, [BulkItemStatus.Completed] = 1,
        }, 10_000);
        _repository.Setup(r => r.RequestCancelAsync("req-1", 1, BulkHandlerKit.UserId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelled);
        _repository.Setup(r => r.RequestCancelWithAuditAsync(
                "req-1", 1, BulkHandlerKit.UserId, It.IsAny<DateTime>(), It.IsAny<BulkAuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, string, DateTime, BulkAuditEvent, CancellationToken>((_, _, _, _, audit, _) => durableAudit = audit)
            .ReturnsAsync(cancelled);

        var detail = await Handler().Handle(Command(), CancellationToken.None);

        detail.CancelRequested.Should().BeTrue();
        detail.Status.Should().Be("active", "files being scored finish before the request completes");
        detail.Counts.Scoring.Should().Be(2, "the detail reports how many are still finishing");
        detail.Counts.Cancelled.Should().Be(7);
        detail.CancelledBy.Should().Be(BulkHandlerKit.UserId, "no user record is available, so the identifier is shown");
        _audit.Should().ContainSingle().Which.Should().Match<ProcessingEvent>(e =>
            e.EventType == "bulk-scoring-request.cancel-requested" && e.EntityId == "req-1" && e.Actor == BulkHandlerKit.UserId && e.CorrelationId == CorrelationId);
        durableAudit.Should().NotBeNull();
        durableAudit!.EventType.Should().Be(BulkAuditEventTypes.BulkRequestCancellationRequested);
        durableAudit.ActorType.Should().Be(BulkAuditActorType.User);
        durableAudit.ActorId.Should().Be(BulkHandlerKit.UserId);
        durableAudit.Boundary.Should().Be(BulkAuditBoundary.Transactional);
    }

    [Fact]
    public async Task Immediate_cancellation_finalization_is_attributed_to_the_explicit_finalizer_service()
    {
        var active = BulkHandlerKit.Request(BulkRequestStatus.Active);
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(active);
        var done = BulkHandlerKit.Request(BulkRequestStatus.Active);
        done.RequestCancel(BulkHandlerKit.UserId, BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        done.ApplyCounts(new Dictionary<BulkItemStatus, int>
        {
            [BulkItemStatus.Cancelled] = 2,
            [BulkItemStatus.Skipped] = 1,
        }, 3_000);
        done.Complete(BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        _repository.Setup(r => r.RequestCancelWithAuditAsync(
                "req-1", 1, BulkHandlerKit.UserId, It.IsAny<DateTime>(), It.IsAny<BulkAuditEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(done);

        await Handler().Handle(Command(), CancellationToken.None);

        _audit.Should().ContainSingle(e => e.EventType == "bulk-scoring-request.completed")
            .Which.Actor.Should().Be(BulkScoringCycle.FinalizationServiceActor);
    }

    [Fact]
    public async Task Scoring_settlement_uses_atomic_item_terminal_events_then_attempts_first_finalization()
    {
        var repository = new Mock<IBulkScoringRepository>();
        var atomic = repository.As<IAtomicBulkScoringItemRepository>();
        var applications = new Mock<IApplicationRepository>();
        var authorizations = new Mock<IBulkRequestAuthorizationRepository>();
        var itemAudits = new List<BulkAuditEvent>();
        var completed = ScoringItem(1, "app-completed");
        var failed = ScoringItem(2, "app-failed");
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);
        request.CorrelationId = Guid.NewGuid().ToString();
        var finalized = BulkHandlerKit.Request(BulkRequestStatus.Active);
        finalized.RequestCancel(BulkHandlerKit.UserId, BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        finalized.ApplyCounts(new Dictionary<BulkItemStatus, int>
        {
            [BulkItemStatus.Completed] = 1,
            [BulkItemStatus.Failed] = 1,
        }, 2_003);
        finalized.Complete(BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);

        repository.Setup(r => r.GetScoringItemsBatchAsync(BulkScoringCycle.SweepBatchSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync([completed, failed]);
        repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(request);
        repository.Setup(r => r.ListActiveRequestIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["req-1"]);
        repository.Setup(r => r.TryFinalizeAsync("req-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(finalized);
        repository.Setup(r => r.PurgeExpiredDraftsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        atomic.Setup(r => r.SaveItemWithAuditAsync(
                It.IsAny<BulkScoringItem>(), It.IsAny<BulkAuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<BulkScoringItem, BulkAuditEvent, CancellationToken>((_, audit, _) => itemAudits.Add(audit))
            .Returns(Task.CompletedTask);
        applications.Setup(r => r.GetByIdAsync("app-completed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TalentMatch.Domain.Entities.Application { Id = "app-completed", Status = "Completed" });
        applications.Setup(r => r.GetByIdAsync("app-failed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TalentMatch.Domain.Entities.Application { Id = "app-failed", Status = "ScoringFailed", LastError = "model refused" });
        authorizations.Setup(r => r.ListDiscardableAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        authorizations.Setup(r => r.ListActiveObtainedBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var cycle = new BulkScoringCycle(
            repository.Object,
            applications.Object,
            authorizations.Object,
            null!,
            new BulkScoringOptions(),
            BulkHandlerKit.Clock,
            Mock.Of<ILogger<BulkScoringCycle>>());

        await cycle.SweepAsync(CancellationToken.None);

        itemAudits.Select(audit => audit.EventType).Should().BeEquivalentTo(
            [BulkAuditEventTypes.BulkItemCompleted, BulkAuditEventTypes.BulkItemFailed]);
        itemAudits.Should().OnlyContain(audit =>
            audit.ActorType == BulkAuditActorType.Service
            && audit.ActorId == BulkScoringCycle.ScoringReconciliationServiceActor
            && audit.InitiatedByActorId == BulkHandlerKit.UserId
            && audit.CorrelationId == request.CorrelationId
            && audit.Boundary == BulkAuditBoundary.Transactional);
        repository.Verify(r => r.TryFinalizeAsync("req-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(BulkAuditEventTypes.BulkItemCancelled, BulkScoringCycle.WorkerServiceActor)]
    [InlineData(BulkAuditEventTypes.BulkItemSkipped, BulkScoringCycle.WorkerServiceActor)]
    [InlineData(BulkAuditEventTypes.BulkRequestCountsReconciled, BulkScoringCycle.CountReconciliationServiceActor)]
    [InlineData(BulkAuditEventTypes.BulkRequestTerminalFinalized, BulkScoringCycle.FinalizationServiceActor)]
    public void Terminal_and_reconciliation_events_require_explicit_service_actors(
        string eventType,
        string serviceActor)
    {
        var audit = BulkAuditWriter.Service(
            eventType,
            serviceActor,
            BulkHandlerKit.UserId,
            Guid.NewGuid().ToString(),
            BulkHandlerKit.Clock.GetUtcNow().UtcDateTime,
            eventType.StartsWith("bulk_item", StringComparison.Ordinal) ? "bulk_item" : "bulk_request",
            "opaque-id",
            new { state = "terminal" });

        audit.ActorType.Should().Be(BulkAuditActorType.Service);
        audit.ActorId.Should().Be(serviceActor);
        audit.InitiatedByActorId.Should().Be(BulkHandlerKit.UserId);
        audit.Boundary.Should().Be(BulkAuditBoundary.Transactional);
        audit.Validate();
    }

    [Fact]
    public async Task Cancelling_twice_returns_the_request_without_changing_or_auditing_again()
    {
        var already = BulkHandlerKit.Request(BulkRequestStatus.Active);
        already.RequestCancel(BulkHandlerKit.UserId, BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(already);

        var detail = await Handler().Handle(Command(version: 99), CancellationToken.None);

        detail.CancelRequested.Should().BeTrue();
        _repository.Verify(r => r.RequestCancelAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Should().BeEmpty();
    }

    [Fact]
    public async Task A_finished_request_cannot_be_cancelled_and_its_outcome_is_untouched()
    {
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(BulkHandlerKit.Request(BulkRequestStatus.Completed));

        var error = (await FluentActions.Awaiting(() => Handler().Handle(Command(), CancellationToken.None)).Should().ThrowAsync<BulkApiException>()).Which;

        (error.StatusCode, error.Code).Should().Be((409, "invalid_state"));
        _repository.Verify(r => r.RequestCancelAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_stale_version_is_a_409_version_conflict_and_nothing_is_audited()
    {
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(BulkHandlerKit.Request(BulkRequestStatus.Active));
        _repository.Setup(r => r.RequestCancelWithAuditAsync(
                "req-1", 5, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<BulkAuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("stale_version"));

        var error = (await FluentActions.Awaiting(() => Handler().Handle(Command(version: 5), CancellationToken.None)).Should().ThrowAsync<BulkApiException>()).Which;

        (error.StatusCode, error.Code).Should().Be((409, "version_conflict"));
        _audit.Should().BeEmpty();
    }

    [Fact]
    public async Task A_preview_is_abandoned_not_cancelled()
    {
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(BulkHandlerKit.Request(BulkRequestStatus.Draft));

        var error = (await FluentActions.Awaiting(() => Handler().Handle(Command(), CancellationToken.None)).Should().ThrowAsync<BulkApiException>()).Which;

        (error.StatusCode, error.Code).Should().Be((409, "invalid_state"));
    }

    [Fact]
    public async Task An_unknown_request_is_a_404()
    {
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync((BulkScoringRequest?)null);

        var error = (await FluentActions.Awaiting(() => Handler().Handle(Command(), CancellationToken.None)).Should().ThrowAsync<BulkApiException>()).Which;

        error.StatusCode.Should().Be(404);
    }

    [Fact]
    public void The_domain_completes_a_cancelled_request_with_outcome_cancelled_once_scoring_settles()
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);
        request.RequestCancel(BulkHandlerKit.UserId, DateTime.UtcNow);
        request.ApplyCounts(new Dictionary<BulkItemStatus, int> { [BulkItemStatus.Scoring] = 1, [BulkItemStatus.Cancelled] = 3 }, 4_000);

        FluentActions.Invoking(() => request.Complete(DateTime.UtcNow)).Should().Throw<InvalidOperationException>("a file is still being scored");

        request.ApplyCounts(new Dictionary<BulkItemStatus, int> { [BulkItemStatus.Completed] = 1, [BulkItemStatus.Cancelled] = 3 }, 4_000);
        request.Complete(DateTime.UtcNow);

        request.Outcome.Should().Be(BulkRequestOutcome.Cancelled);
        request.CompletedCount.Should().Be(1, "completed outcomes are unchanged by a cancel");
    }

    private static BulkScoringItem ScoringItem(int ordinal, string applicationId)
    {
        var item = BulkHandlerKit.Item(ordinal, BulkItemStatus.Scoring);
        item.LinkApplication(applicationId, $"doc-{ordinal}", BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        return item;
    }
}
