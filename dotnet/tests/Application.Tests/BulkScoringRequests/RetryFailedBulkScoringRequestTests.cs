using FluentAssertions;
using Moq;
using System.Text.Json;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class RetryFailedBulkScoringRequestTests
{
    private readonly Mock<IBulkScoringRepository> _repository = new();
    private readonly Mock<IProcessingEventRepository> _events = new();
    private readonly Mock<ISharePointSource> _source = new();
    private readonly Mock<IRequestAuthorizationStore> _store = new();
    private readonly List<ProcessingEvent> _audit = [];
    private IReadOnlyCollection<BulkAuditEvent> _typedAudit = [];
    private BulkScoringRequest? _created;
    private IReadOnlyList<BulkScoringItem> _createdItems = [];
    private BulkRequestAuthorization? _createdAuthorization;

    public RetryFailedBulkScoringRequestTests()
    {
        _events.Setup(e => e.AddAsync(It.IsAny<ProcessingEvent>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingEvent, CancellationToken>((evt, _) => _audit.Add(evt))
            .Returns(Task.CompletedTask);
        _repository.Setup(r => r.CreateRetryRequestWithAuditAsync(
                It.IsAny<BulkScoringRequest>(), It.IsAny<IReadOnlyList<BulkScoringItem>>(), It.IsAny<BulkRequestAuthorization?>(),
                It.IsAny<IReadOnlyCollection<BulkAuditEvent>>(), It.IsAny<CancellationToken>()))
            .Returns<BulkScoringRequest, IReadOnlyList<BulkScoringItem>, BulkRequestAuthorization?, IReadOnlyCollection<BulkAuditEvent>, CancellationToken>(
                (retry, items, authorization, audit, _) =>
            {
                foreach (var item in items) item.RequestId = retry.Id;
                retry.ApplyItemCounts(items);
                (_created, _createdItems, _createdAuthorization) = (retry, items, authorization);
                _typedAudit = audit;
                return Task.FromResult(retry);
            });
        _store.Setup(s => s.TransferToRequestAsync(
                It.IsAny<string>(), "retry-connection", BulkHandlerKit.UserId, "job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string requestId, string _, string actorId, string _, CancellationToken _) =>
                BulkRequestAuthorization.Establish(
                    requestId,
                    actorId,
                    BulkHandlerKit.TenantId,
                    "fresh-account-binding",
                    "drive",
                    "folder",
                    [1],
                    [2],
                    "v1",
                    BulkHandlerKit.Clock.GetUtcNow().UtcDateTime));
        _source.Setup(s => s.ResolveFolderAsync(It.IsAny<string>(), It.IsAny<SourceCallContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceFolder(BulkSourceKind.SharePoint, "drive", "folder", "Candidates", "/Candidates", "https://x", BulkHandlerKit.TenantId));
        _source.Setup(s => s.GetItemMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SourceCallContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceItemMetadata("etag-now", 10));
        _source.Setup(s => s.GetItemMetadataAsync(It.IsAny<SourceFolder>(), It.IsAny<string>(), It.IsAny<SourceCallContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceItemMetadata("etag-now", 10));
    }

    private BulkScoringRequest Source(IEnumerable<BulkScoringItem> items, bool raster = false, bool duplicates = false)
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Completed, raster: raster, duplicates: duplicates);
        var all = items.ToList();
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(request);
        // Mirrors the repository: only pre-hand-off failures and cancellations with a retryable reason.
        _repository.Setup(r => r.GetRetryableItemsAsync("req-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => all.Where(i => i.IsRetryable).ToList());
        return request;
    }

    private RetryFailedBulkScoringRequestItemsHandler Handler(Dictionary<string, string?>? settings = null, string? assertion = "user-token", bool withStore = true) =>
        new(BulkHandlerKit.Access(_source.Object, settings, assertion: assertion), _repository.Object, _source.Object, _events.Object,
            BulkHandlerKit.Jobs().Object, BulkHandlerKit.Clock, withStore ? _store.Object : null);

    private static RetryFailedBulkScoringRequestItemsCommand Command() =>
        new("req-1", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "retry-connection");

    private static async Task<BulkApiException> Failure(Task<BulkRequestDetailDto> call) =>
        (await FluentActions.Awaiting(() => call).Should().ThrowAsync<BulkApiException>()).Which;

    [Fact]
    public async Task Only_pre_hand_off_failures_with_a_retryable_reason_are_included()
    {
        var handedOff = DateTime.UtcNow;
        var source = Source(
        [
            BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.TransientExhausted),
            BulkHandlerKit.Item(1, BulkItemStatus.Failed, BulkReasonCode.SourceChanged),
            BulkHandlerKit.Item(2, BulkItemStatus.Failed, BulkReasonCode.ConversionTimeout),
            BulkHandlerKit.Item(3, BulkItemStatus.Failed, BulkReasonCode.AuthorizationLapsed),
            BulkHandlerKit.Item(4, BulkItemStatus.Cancelled, BulkReasonCode.CancelledByUser),
            BulkHandlerKit.Item(5, BulkItemStatus.Failed, BulkReasonCode.ScoringFailed),
            BulkHandlerKit.Item(6, BulkItemStatus.Failed, BulkReasonCode.ConversionFailed),
            BulkHandlerKit.Item(7, BulkItemStatus.Failed, BulkReasonCode.FileTooLarge),
            BulkHandlerKit.Item(8, BulkItemStatus.Failed, BulkReasonCode.TransientExhausted, handedOffAt: handedOff),
            BulkHandlerKit.Item(9, BulkItemStatus.Completed),
            BulkHandlerKit.Item(10, BulkItemStatus.Skipped, BulkReasonCode.DuplicateInJob),
        ]);

        var detail = await Handler().Handle(Command(), CancellationToken.None);

        _createdItems.Select(i => i.RetryOfItemId).Should().BeEquivalentTo("item-0", "item-1", "item-2", "item-3", "item-4");
        detail.Counts.Discovered.Should().Be(5);
        detail.Counts.Waiting.Should().Be(5);
        detail.RetryOfRequestId.Should().Be(source.Id);
    }

    [Fact]
    public async Task A_missing_or_unreadable_file_is_retried_only_when_it_resolves_again()
    {
        Source(
        [
            BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.SourceMissing),
            BulkHandlerKit.Item(1, BulkItemStatus.Failed, BulkReasonCode.SourceMissing),
            BulkHandlerKit.Item(2, BulkItemStatus.Failed, BulkReasonCode.AccessDenied),
            BulkHandlerKit.Item(3, BulkItemStatus.Failed, BulkReasonCode.AccessDenied),
            BulkHandlerKit.Item(4, BulkItemStatus.Failed, BulkReasonCode.TransientExhausted),
        ]);
        // 0 and 2 resolve now; 1 is still gone; 3 is still not readable by this recruiter.
        _source.Setup(s => s.GetItemMetadataAsync(It.IsAny<SourceFolder>(), "src-1", It.IsAny<SourceCallContext>(), It.IsAny<CancellationToken>())).ReturnsAsync((SourceItemMetadata?)null);
        _source.Setup(s => s.GetItemMetadataAsync(It.IsAny<SourceFolder>(), "src-3", It.IsAny<SourceCallContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(new SourceAccessDeniedException("no"));

        await Handler().Handle(Command(), CancellationToken.None);

        _createdItems.Select(i => i.RetryOfItemId).Should().BeEquivalentTo("item-0", "item-2", "item-4");
        _source.Verify(s => s.GetItemMetadataAsync(It.IsAny<SourceFolder>(), "src-4", It.IsAny<SourceCallContext>(), It.IsAny<CancellationToken>()), Times.Never,
            "other reasons need no resolution check");
    }

    [Fact]
    public async Task The_new_request_inherits_the_choices_and_limits_records_the_current_configuration_and_leaves_the_source_alone()
    {
        var item = BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.TransientExhausted);
        var source = Source([item], raster: true, duplicates: true);
        var before = (source.Status, source.FailedCount, source.ConcurrencyVersion, source.CapturedScoringConfigVersionId);

        var detail = await Handler().Handle(Command(), CancellationToken.None);

        _created!.Id.Should().NotBe(source.Id);
        _created.Status.Should().Be(BulkRequestStatus.Active);
        _created.ConvertToRasterPdf.Should().BeTrue();
        _created.AllowDuplicates.Should().BeTrue();
        (_created.MaxFilesPerRequest, _created.MaxBytesPerFile, _created.MaxTotalBytes,
            _created.DiscoveryStepMaxFiles, _created.DiscoveryStepMaxEntries, _created.DiscoveryStepMaxSeconds)
            .Should().Be((
                source.MaxFilesPerRequest, source.MaxBytesPerFile, source.MaxTotalBytes,
                source.DiscoveryStepMaxFiles, source.DiscoveryStepMaxEntries, source.DiscoveryStepMaxSeconds));
        _created.CapturedScoringConfigVersionId.Should().Be(BulkHandlerKit.CurrentConfig, "the retry scores with the configuration current now");
        _created.OwnerActorId.Should().Be(BulkHandlerKit.UserId);
        detail.Status.Should().Be("active");
        detail.ConvertToRasterPdf.Should().BeTrue();
        detail.AllowDuplicates.Should().BeTrue();

        (source.Status, source.FailedCount, source.ConcurrencyVersion, source.CapturedScoringConfigVersionId).Should().Be(before);
        item.Status.Should().Be(BulkItemStatus.Failed, "the source item is never changed");
        item.RequestId.Should().Be("req-1");
    }

    [Fact]
    public async Task Retried_items_start_waiting_without_an_etag_so_the_worker_records_the_current_one()
    {
        Source([BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.SourceChanged)]);

        await Handler().Handle(Command(), CancellationToken.None);

        var retried = _createdItems.Single();
        retried.Status.Should().Be(BulkItemStatus.Waiting);
        retried.SourceETag.Should().BeEmpty();
        retried.ReasonCode.Should().BeNull();
        retried.ApplicationId.Should().BeNull("an application is created only by the retry's own import");
        retried.Id.Should().NotBe("item-0");
        retried.SourceDriveItemId.Should().Be("src-0");
        retried.RequestId.Should().Be(_created!.Id);
    }

    [Fact]
    public async Task Nothing_retryable_is_a_422_and_creates_neither_a_request_nor_an_authorization()
    {
        Source(
        [
            BulkHandlerKit.Item(0, BulkItemStatus.Completed),
            BulkHandlerKit.Item(1, BulkItemStatus.Failed, BulkReasonCode.ScoringFailed),
            BulkHandlerKit.Item(2, BulkItemStatus.Failed, BulkReasonCode.FileTooLarge),
        ]);

        var error = await Failure(Handler().Handle(Command(), CancellationToken.None));

        (error.StatusCode, error.Code).Should().Be((422, "no_retryable_bulk_files"));
        _created.Should().BeNull();
        _store.Verify(s => s.TransferToRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_request_that_is_still_running_cannot_be_retried()
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);
        _repository.Setup(r => r.GetAsync("req-1", It.IsAny<CancellationToken>())).ReturnsAsync(request);

        var error = await Failure(Handler().Handle(Command(), CancellationToken.None));

        (error.StatusCode, error.Code).Should().Be((409, "invalid_state"));
    }

    [Fact]
    public async Task Same_tenant_access_is_checked_first_as_the_retrying_recruiter_and_a_refusal_is_a_403_with_nothing_created()
    {
        Source([BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.TransientExhausted)]);
        _source.Setup(s => s.ResolveFolderAsync(It.IsAny<string>(), It.IsAny<SourceCallContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SourceAccessDeniedException("no"));

        var error = await Failure(Handler().Handle(Command(), CancellationToken.None));

        (error.StatusCode, error.Code).Should().Be((403, "sharepoint_access_denied"));
        _created.Should().BeNull();
        _store.Verify(s => s.TransferToRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _source.Verify(s => s.ResolveFolderAsync(It.IsAny<string>(), It.Is<SourceCallContext>(c => c.Phase == SourcePhase.Interactive && c.RecruiterActorId == BulkHandlerKit.UserId), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Retry_consumes_a_new_verified_connection_without_reusing_the_old_authorization()
    {
        Source([BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.AuthorizationLapsed)]);

        await Handler(assertion: "fresh-token").Handle(Command(), CancellationToken.None);

        _createdAuthorization.Should().NotBeNull();
        _createdAuthorization!.RequestId.Should().Be(_created!.Id, "the authorization belongs to the new request, not the lapsed one");
        _createdAuthorization.IsBoundTo(
            _created.Id,
            BulkHandlerKit.UserId,
            BulkHandlerKit.TenantId,
            "fresh-account-binding",
            "drive",
            "folder").Should().BeTrue();
        _created.SourceAccountBindingHash.Should().Be("fresh-account-binding");
        _store.Verify(s => s.TransferToRequestAsync(
            _created.Id,
            "retry-connection",
            BulkHandlerKit.UserId,
            "job-1",
            It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.AcquireGraphTokenAsync("req-1", It.IsAny<CancellationToken>()), Times.Never,
            "the original request cache must never authorize a retry");
        _source.Verify(s => s.ResolveFolderAsync(
            It.IsAny<string>(),
            It.Is<SourceCallContext>(c =>
                c.Phase == SourcePhase.Interactive
                && c.ConnectionId == "retry-connection"
                && c.RequestId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_the_session_cannot_support_an_authorization_the_answer_is_401_and_nothing_is_created()
    {
        Source([BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.AuthorizationLapsed)]);
        _store.Setup(s => s.TransferToRequestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthorizationRequiredException("sign in again"));

        var error = await Failure(Handler().Handle(Command(), CancellationToken.None));

        (error.StatusCode, error.Code).Should().Be((401, "authorization_required"));
        _created.Should().BeNull();
        _audit.Should().BeEmpty();
    }

    [Fact]
    public async Task A_successful_retry_is_audited_with_the_request_it_came_from()
    {
        Source(
        [
            BulkHandlerKit.Item(0, BulkItemStatus.Failed, BulkReasonCode.TransientExhausted),
            BulkHandlerKit.Item(1, BulkItemStatus.Cancelled, BulkReasonCode.CancelledByUser),
        ]);

        await Handler().Handle(Command(), CancellationToken.None);

        _audit.Should().Contain(e => e.EventType == "bulk-scoring-authorization.created" && e.EntityId == _created!.Id && e.Actor == BulkHandlerKit.UserId);
        _audit.Single(e => e.EventType == "bulk-scoring-request.retry-created").Should().Match<ProcessingEvent>(e =>
            e.EntityId == _created!.Id && e.Actor == BulkHandlerKit.UserId && e.PayloadJson.Contains("req-1"));

        var requestAudit = _typedAudit.Single(e => e.EventType == BulkAuditEventTypes.BulkRequestRetryCreated);
        requestAudit.ActorType.Should().Be(BulkAuditActorType.User);
        requestAudit.ActorId.Should().Be(BulkHandlerKit.UserId);
        requestAudit.CorrelationId.Should().Be("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        requestAudit.SubjectId.Should().Be(_created!.Id);
        JsonDocument.Parse(requestAudit.SafeFactsJson).RootElement
            .GetProperty("retryOfRequestId").GetString().Should().Be("req-1");

        var itemAudits = _typedAudit.Where(e => e.EventType == BulkAuditEventTypes.BulkRetryItemCreated).ToArray();
        itemAudits.Should().HaveCount(2);
        itemAudits.Should().OnlyContain(e =>
            e.ActorType == BulkAuditActorType.User
            && e.ActorId == BulkHandlerKit.UserId
            && e.CorrelationId == "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
            && _createdItems.Any(item => item.Id == e.SubjectId));
        itemAudits.Select(e => JsonDocument.Parse(e.SafeFactsJson).RootElement
                .GetProperty("retryOfItemId").GetString())
            .Should().BeEquivalentTo("item-0", "item-1");
    }

    [Fact]
    public void An_application_identity_is_write_once_on_a_retried_item()
    {
        var item = BulkHandlerKit.Item(0, BulkItemStatus.Scoring);
        item.LinkApplication("app-1", "doc-1", DateTime.UtcNow);

        item.LinkApplication("app-1", "doc-1", DateTime.UtcNow);
        FluentActions.Invoking(() => item.LinkApplication("app-2", "doc-2", DateTime.UtcNow)).Should().Throw<InvalidOperationException>();

        item.CreateRetry(DateTime.UtcNow).ApplicationId.Should().BeNull();
    }
}
