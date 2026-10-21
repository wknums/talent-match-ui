using FluentAssertions;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

// The scoring configuration can change while a request runs. That is only reported, never acted on.
public sealed class ConfigVersionNoticeTests
{
    private static Job JobAt(string? version) => new() { Id = "job-1", Title = "Job", CurrentConfigVersionId = version };

    [Theory]
    [InlineData("config-1", "config-1", false)]
    [InlineData("config-1", "config-2", true)]
    [InlineData(null, "config-2", false)]
    public async Task The_notice_is_set_only_when_the_current_version_differs_from_the_one_recorded_at_acceptance(string? recorded, string current, bool expected)
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);
        request.JobConfigVersionId = recorded;

        var detail = await BulkHandlerKit.Access().DetailAsync(request, JobAt(current), null, CancellationToken.None);

        detail.ConfigChangedSinceAcceptance.Should().Be(expected);
        detail.JobConfigVersionId.Should().Be(recorded);
    }

    [Fact]
    public async Task A_deleted_or_unreadable_job_never_raises_the_notice()
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);

        (await BulkHandlerKit.Access().DetailAsync(request, null, null, CancellationToken.None)).ConfigChangedSinceAcceptance.Should().BeFalse();
    }

    [Fact]
    public async Task The_notice_changes_nothing_about_the_request_or_its_files()
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active);
        request.ApplyCounts(new Dictionary<BulkItemStatus, int> { [BulkItemStatus.Waiting] = 4, [BulkItemStatus.Completed] = 1 }, 5_000);

        var detail = await BulkHandlerKit.Access().DetailAsync(request, JobAt("config-9"), null, CancellationToken.None);

        detail.ConfigChangedSinceAcceptance.Should().BeTrue();
        detail.Status.Should().Be("active");
        detail.Counts.Waiting.Should().Be(4);
        request.Status.Should().Be(BulkRequestStatus.Active);
    }
}
