using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class BulkScoringConfigurationVersionTests
{
    private const string CapturedVersion = "config-at-confirmation";
    private const string CurrentVersion = "config-at-handoff";

    [Fact]
    public void Confirmation_captures_the_jobs_current_version_as_immutable_request_provenance()
    {
        var job = JobAt(CapturedVersion);
        var request = BulkHandlerKit.Request(BulkRequestStatus.Draft);
        request.ApplyCounts(new Dictionary<BulkItemStatus, int> { [BulkItemStatus.Waiting] = 1 }, 1_000);

        request.Confirm(false, false, job.CurrentConfigVersionId, BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        job.CurrentConfigVersionId = CurrentVersion;

        request.CapturedScoringConfigVersionId.Should().Be(CapturedVersion);
    }

    [Fact]
    public async Task Scoring_handoff_resolves_the_jobs_current_version_instead_of_forcing_the_captured_version()
    {
        var scenario = HandoffScenario();

        var outcome = await scenario.Processor.ProcessAsync(scenario.Item, scenario.Request, CancellationToken.None);

        outcome.Should().Be(BulkItemOutcome.HandedOff);
        scenario.Item.ScoringConfigVersionId.Should().Be(CurrentVersion);
        scenario.Item.ScoringConfigVersionId.Should().NotBe(scenario.Request.CapturedScoringConfigVersionId);
    }

    [Fact]
    public async Task The_version_used_at_scoring_handoff_is_persisted_per_item()
    {
        var scenario = HandoffScenario();

        await scenario.Processor.ProcessAsync(scenario.Item, scenario.Request, CancellationToken.None);

        scenario.Persisted.Should().NotBeNull();
        scenario.Persisted!.ScoringConfigVersionId.Should().Be(CurrentVersion);
        scenario.Persisted.HandedOffAt.Should().Be(BulkHandlerKit.Clock.GetUtcNow().UtcDateTime);
        scenario.Persisted.Status.Should().Be(BulkItemStatus.Scoring);
    }

    [Fact]
    public async Task Detail_raises_the_changed_version_notice_when_the_current_version_differs_from_captured()
    {
        var request = ActiveRequest(CapturedVersion);
        request.Items.Add(ItemUsing(CapturedVersion));

        var detail = await BulkHandlerKit.Access().DetailAsync(
            request, JobAt(CurrentVersion), null, CancellationToken.None);

        detail.ConfigChangedSinceAcceptance.Should().BeTrue();
    }

    [Fact]
    public async Task Detail_raises_the_changed_version_notice_when_any_used_version_differs_from_captured()
    {
        var request = ActiveRequest(CapturedVersion);
        request.Items.Add(ItemUsing(CurrentVersion));

        var detail = await BulkHandlerKit.Access().DetailAsync(
            request, JobAt(CapturedVersion), null, CancellationToken.None);

        detail.ConfigChangedSinceAcceptance.Should().BeTrue();
    }

    [Fact]
    public async Task Matching_captured_current_and_used_versions_leave_status_counts_and_notice_unchanged()
    {
        var request = ActiveRequest(CapturedVersion);
        request.ApplyCounts(new Dictionary<BulkItemStatus, int> { [BulkItemStatus.Scoring] = 1 }, 1_001);
        request.Items.Add(ItemUsing(CapturedVersion));

        var detail = await BulkHandlerKit.Access().DetailAsync(
            request, JobAt(CapturedVersion), null, CancellationToken.None);

        detail.ConfigChangedSinceAcceptance.Should().BeFalse();
        detail.Status.Should().Be("active");
        detail.Counts.Scoring.Should().Be(1);
        request.Status.Should().Be(BulkRequestStatus.Active);
        request.Items.Should().ContainSingle().Which.ScoringConfigVersionId.Should().Be(CapturedVersion);
    }

    [Fact]
    public async Task Failed_handoff_persistence_rolls_back_publication_timestamp_and_used_version_together()
    {
        var request = ActiveRequest(CapturedVersion);
        request.Job = JobAt(CurrentVersion);
        var item = ReadyItem();
        request.Items.Add(item);
        var persisted = Snapshot(item);
        var published = false;
        var repository = new Mock<IBulkScoringRepository>();
        repository.Setup(r => r.SaveItemWithAuditAsync(
                It.IsAny<BulkScoringItem>(), It.IsAny<ProcessingEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handoff transaction failed"));
        repository.Setup(r => r.SaveItemAsync(It.IsAny<BulkScoringItem>(), It.IsAny<CancellationToken>()))
            .Callback<BulkScoringItem, CancellationToken>((saved, _) => persisted = Snapshot(saved))
            .Returns(Task.CompletedTask);
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(a => a.PublishUploadedAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => published = true)
            .Returns(Task.CompletedTask);
        var processor = Processor(repository.Object, applications.Object);

        _ = await Record.ExceptionAsync(
            () => processor.ProcessAsync(item, request, CancellationToken.None));

        persisted.Status.Should().Be(BulkItemStatus.Importing);
        persisted.HandedOffAt.Should().BeNull();
        persisted.ScoringConfigVersionId.Should().BeNull();
        published.Should().BeFalse("application publication and item handoff provenance are one atomic boundary");
    }

    private static HandoffHarness HandoffScenario()
    {
        var request = ActiveRequest(CapturedVersion);
        request.Job = JobAt(CurrentVersion);
        var item = ReadyItem();
        request.Items.Add(item);
        ItemSnapshot? persisted = null;
        var repository = new Mock<IBulkScoringRepository>();
        repository.Setup(r => r.SaveItemWithAuditAsync(
                It.IsAny<BulkScoringItem>(), It.IsAny<ProcessingEvent>(), It.IsAny<CancellationToken>()))
            .Callback<BulkScoringItem, ProcessingEvent, CancellationToken>(
                (saved, _, _) => persisted = Snapshot(saved))
            .Returns(Task.CompletedTask);
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(a => a.PublishUploadedAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new HandoffHarness(
            request,
            item,
            Processor(repository.Object, applications.Object),
            () => persisted);
    }

    private static BulkItemProcessor Processor(
        IBulkScoringRepository repository,
        IApplicationRepository applications) =>
        new(
            repository,
            applications,
            Mock.Of<ISharePointSource>(),
            Mock.Of<IBulkContentStore>(),
            Mock.Of<IDocumentRasterizer>(),
            new BulkScoringOptions(),
            BulkHandlerKit.Clock,
            NullLogger<BulkItemProcessor>.Instance);

    private static BulkScoringRequest ActiveRequest(string capturedVersion)
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);
        request.CapturedScoringConfigVersionId = capturedVersion;
        return request;
    }

    private static Job JobAt(string version) => new()
    {
        Id = "job-1",
        Title = "Job",
        CurrentConfigVersionId = version,
    };

    private static BulkScoringItem ReadyItem()
    {
        var item = BulkHandlerKit.Item(1, BulkItemStatus.Importing);
        item.LinkApplication(
            DeterministicIds.Application(item.Id),
            DeterministicIds.Document(item.Id),
            BulkHandlerKit.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
        return item;
    }

    private static BulkScoringItem ItemUsing(string version)
    {
        var item = BulkHandlerKit.Item(1, BulkItemStatus.Scoring);
        item.ScoringConfigVersionId = version;
        return item;
    }

    private static ItemSnapshot Snapshot(BulkScoringItem item) =>
        new(item.Status, item.HandedOffAt, item.ScoringConfigVersionId);

    private sealed record ItemSnapshot(
        BulkItemStatus Status,
        DateTime? HandedOffAt,
        string? ScoringConfigVersionId);

    private sealed record HandoffHarness(
        BulkScoringRequest Request,
        BulkScoringItem Item,
        BulkItemProcessor Processor,
        Func<ItemSnapshot?> PersistedValue)
    {
        public ItemSnapshot? Persisted => PersistedValue();
    }
}
