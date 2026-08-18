using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public class ScoringBatchRepositoryTests
{
    [Fact]
    public async Task ScheduleResubmissionAsync_PreservesPriorSubmissionAndIncrementsAttempt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var batch = new ScoringBatch
        {
            JobId = "job-1",
            PromptVersionId = "prompt-1",
            ApplicationIdsJson = """["app-1"]""",
            Status = "submitted",
            SubmissionId = "submission-1",
            PollUrl = "/status/submission-1",
            Attempt = 2,
        };
        context.ScoringBatches.Add(batch);
        await context.SaveChangesAsync();

        var repository = new ScoringBatchRepository(context);
        var nextAttemptAt = DateTime.UtcNow.AddMinutes(1);

        await repository.ScheduleResubmissionAsync(
            batch.Id, "invalid JSON response", nextAttemptAt);

        context.ChangeTracker.Clear();
        var reloaded = await context.ScoringBatches.SingleAsync(x => x.Id == batch.Id);
        reloaded.Status.Should().Be("pending");
        reloaded.Attempt.Should().Be(3);
        reloaded.SubmissionId.Should().Be("submission-1");
        reloaded.PollUrl.Should().BeNull();
        reloaded.LastError.Should().Be("invalid JSON response");
        reloaded.NextPollAt.Should().BeCloseTo(nextAttemptAt, TimeSpan.FromSeconds(1));
    }
}
