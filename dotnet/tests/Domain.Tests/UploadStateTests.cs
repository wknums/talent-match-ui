using FluentAssertions;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Tests;

public sealed class UploadStateTests
{
    [Theory]
    [InlineData(UploadItemStatus.Waiting, UploadItemStatus.Throttled)]
    [InlineData(UploadItemStatus.Waiting, UploadItemStatus.Uploading)]
    [InlineData(UploadItemStatus.Throttled, UploadItemStatus.Waiting)]
    [InlineData(UploadItemStatus.Uploading, UploadItemStatus.Retrying)]
    [InlineData(UploadItemStatus.Uploading, UploadItemStatus.Succeeded)]
    [InlineData(UploadItemStatus.Uploading, UploadItemStatus.SkippedDuplicate)]
    [InlineData(UploadItemStatus.Retrying, UploadItemStatus.Uploading)]
    public void LegalTransitions_AreAccepted(UploadItemStatus from, UploadItemStatus to)
    {
        var item = new UploadItem { Status = from };
        item.TransitionTo(to, DateTime.UtcNow);
        item.Status.Should().Be(to);
    }

    [Theory]
    [InlineData(UploadItemStatus.Succeeded)]
    [InlineData(UploadItemStatus.SkippedDuplicate)]
    [InlineData(UploadItemStatus.Failed)]
    [InlineData(UploadItemStatus.Interrupted)]
    public void TerminalStates_RejectEveryLaterTransition(UploadItemStatus terminal)
    {
        var item = new UploadItem { Status = terminal };
        var act = () => item.TransitionTo(UploadItemStatus.Waiting, DateTime.UtcNow);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AggregateCompletesOnlyWhenEveryItemIsTerminal()
    {
        var session = new UploadSession
        {
            FileConcurrency = 4,
            MaxIndividualFileBytes = 4_194_304,
            MaxInFlightBytes = 104_857_600,
        };
        session.ApplyAggregates(
            [new() { Status = UploadItemStatus.Succeeded }, new() { Status = UploadItemStatus.Failed }],
            DateTime.UtcNow);
        session.ValidateInvariants();
        session.Status.Should().Be(UploadSessionStatuses.Completed);
        session.TerminalItemCount.Should().Be(2);
        session.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public void AttemptCount_CannotExceedFour()
    {
        var item = new UploadItem();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            item.TransitionTo(UploadItemStatus.Uploading, DateTime.UtcNow);
            if (attempt < 3)
                item.TransitionTo(UploadItemStatus.Retrying, DateTime.UtcNow);
        }
        item.AttemptCount.Should().Be(4);
        var act = () =>
        {
            item.TransitionTo(UploadItemStatus.Retrying, DateTime.UtcNow);
            item.TransitionTo(UploadItemStatus.Uploading, DateTime.UtcNow);
        };
        act.Should().Throw<InvalidOperationException>();
    }
}
