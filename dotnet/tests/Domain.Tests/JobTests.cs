using FluentAssertions;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Tests;

public class JobTests
{
    [Fact]
    public void Job_DefaultValues_AreCorrect()
    {
        var job = new Job();
        job.Id.Should().NotBeNullOrEmpty();
        job.Status.Should().Be("active");
        job.Applications.Should().BeEmpty();
        job.ConfigVersions.Should().BeEmpty();
    }

    [Fact]
    public void Job_CanAddConfigVersions()
    {
        var job = new Job { Title = "Test Job" };
        var config = new JobConfigVersion
        {
            JobId = job.Id,
            VersionNumber = 1,
            ScoringRunCount = 3,
            AggregationStrategy = "median"
        };
        job.ConfigVersions.Add(config);
        job.ConfigVersions.Should().HaveCount(1);
    }

    [Fact]
    public void Application_DefaultStatus_IsQueued()
    {
        var app = new Application();
        app.Status.Should().Be("Queued");
        app.FinalScore.Should().BeNull();
        app.FinalDecision.Should().BeNull();
    }

    [Fact]
    public void JobConfigVersion_DefaultValues_AreCorrect()
    {
        var config = new JobConfigVersion();
        config.ScoringRunCount.Should().Be(3);
        config.AggregationStrategy.Should().Be("median");
        config.LonglistThreshold.Should().Be(70);
        config.ShortlistThreshold.Should().Be(85);
        config.VarianceThreshold.Should().Be(15);
    }

    [Fact]
    public void ScoringRun_DefaultValues_AreCorrect()
    {
        var run = new ScoringRun();
        run.CategoryScoresJson.Should().Be("{}");
        run.EvidenceCitationsJson.Should().Be("[]");
    }
}
