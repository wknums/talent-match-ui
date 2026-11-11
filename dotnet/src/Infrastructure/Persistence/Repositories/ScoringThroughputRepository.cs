using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class ScoringThroughputRepository(AppDbContext db) : IScoringThroughputRepository
{
    public async Task<IReadOnlyDictionary<int, int>> GetHourlyCountsAsync(
        IReadOnlyCollection<string> jobIds, DateTime asOfUtc, CancellationToken ct = default)
    {
        if (jobIds.Count == 0)
            return new Dictionary<int, int>();

        var start = asOfUtc.AddHours(-24);
        var minuteOffset = -asOfUtc.Minute;
        var secondOffset = -asOfUtc.Second;
        var normalizeSqliteDates = db.Database.IsSqlite();
        var assessments = db.AggregatedResults.Select(result => new
        {
            result.ApplicationId,
            // The shared SQLite database may contain ISO timestamps written by Stack A.
            CompletedAt = normalizeSqliteDates ? result.CreatedAt.AddSeconds(0) : result.CreatedAt,
        });

        // AggregatedResults has one row per application and preserves CreatedAt on updates.
        // Shift to whole-hour boundaries before grouping so the last bar is exactly the rolling hour.
        return await (
            from result in assessments
            join application in db.Applications on result.ApplicationId equals application.Id
            where application.TestRunId == null
                && jobIds.Contains(application.JobId)
                && result.CompletedAt >= start && result.CompletedAt < asOfUtc
            group result by result.CompletedAt.AddMinutes(minuteOffset).AddSeconds(secondOffset).Hour into hourly
            select new { Hour = hourly.Key, Count = hourly.Count() })
            .ToDictionaryAsync(row => row.Hour, row => row.Count, ct);
    }
}
