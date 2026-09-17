using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public sealed class ScoringThroughputRepositoryTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 7, 32, 7, DateTimeKind.Utc);

    [Fact]
    public async Task Counts_ExactRollingBoundariesAcrossMidnight_ExcludesTestsAndHiddenJobs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Jobs.AddRange(new Job { Id = "visible", JobCode = "visible" }, new Job { Id = "hidden", JobCode = "hidden" });
        AddScored(db, "start", Now.AddHours(-24));
        AddScored(db, "too-old", Now.AddHours(-24).AddMilliseconds(-1));
        AddScored(db, "previous-hour", Now.AddHours(-1).AddMilliseconds(-1));
        AddScored(db, "hour-start", Now.AddHours(-1));
        AddScored(db, "just-finished", Now.AddMilliseconds(-1));
        AddScored(db, "future", Now);
        AddScored(db, "test", Now.AddMinutes(-5), testRunId: "test-1");
        AddScored(db, "hidden-app", Now.AddMinutes(-5), jobId: "hidden");
        db.Applications.Add(new Domain.Entities.Application { Id = "failed", JobId = "visible", Status = "ScoringFailed" });
        await db.SaveChangesAsync();

        var counts = await new ScoringThroughputRepository(db).GetHourlyCountsAsync(["visible"], Now);

        counts.Values.Sum().Should().Be(4);
        counts[7].Should().Be(1);
        counts[5].Should().Be(1);
        counts[6].Should().Be(2);
        counts.Should().HaveCount(3);
    }

    [Fact]
    public async Task Counts_ReadsExistingIsoTimestampsFromTheSharedSqliteDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Jobs.Add(new Job { Id = "visible" });
        AddScored(db, "app-1", Now.AddMinutes(-10));
        await db.SaveChangesAsync();
        var isoTimestamp = Now.AddMinutes(-10).ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AggregatedResults SET CreatedAt = {isoTimestamp} WHERE ApplicationId = {"app-1"}");

        var counts = await new ScoringThroughputRepository(db).GetHourlyCountsAsync(["visible"], Now);

        counts.Values.Sum().Should().Be(1);
        counts[6].Should().Be(1);
    }

    [Fact]
    public async Task UpdatingAnAssessment_DoesNotCountItAgainOrMoveItsCompletionTimestamp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Jobs.Add(new Job { Id = "visible" });
        AddScored(db, "app-1", Now.AddHours(-5));
        await db.SaveChangesAsync();
        await new ApplicationRepository(db).SetAggregatedResultAsync(new AggregatedResult
        {
            ApplicationId = "app-1", CreatedAt = Now.AddMinutes(-5), FinalScore = 95, Decision = "Eligible",
        });

        var counts = await new ScoringThroughputRepository(db).GetHourlyCountsAsync(["visible"], Now);

        counts.Values.Sum().Should().Be(1);
        counts[2].Should().Be(1);
        counts.GetValueOrDefault(6).Should().Be(0);
        (await db.AggregatedResults.SingleAsync()).CreatedAt.Should().Be(Now.AddHours(-5));
    }

    [Fact]
    public async Task NoAccessibleJobs_ReturnsNoCounts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        var result = await new ScoringThroughputRepository(db).GetHourlyCountsAsync([], Now);

        result.Should().BeEmpty();
    }

    private static void AddScored(AppDbContext db, string id, DateTime at, string jobId = "visible", string? testRunId = null)
    {
        db.Applications.Add(new Domain.Entities.Application
        {
            Id = id, JobId = jobId, Status = "NeedsManualReview", TestRunId = testRunId,
        });
        db.AggregatedResults.Add(new AggregatedResult
        {
            ApplicationId = id, CreatedAt = at, FinalScore = 75, Decision = "NeedsManualReview",
        });
    }
}
