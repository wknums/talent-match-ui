using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Stats.Queries;

/// <summary>Unique production applications first scored in a one-hour window.</summary>
public sealed record ScoringThroughputBucketDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Count);

/// <summary>Rolling-hour throughput and 24 hourly windows for the caller's accessible jobs.</summary>
public sealed record ScoringThroughputDto(
    DateTimeOffset AsOfUtc, int ScoredLastHour, int ScoredLast24Hours,
    IReadOnlyList<ScoringThroughputBucketDto> Hours);

public sealed record GetScoringThroughputQuery : IRequest<ScoringThroughputDto>;

public sealed class GetScoringThroughputQueryHandler(
    IJobRepository jobs,
    ICurrentUserService user,
    IScoringThroughputRepository throughput,
    TimeProvider clock) : IRequestHandler<GetScoringThroughputQuery, ScoringThroughputDto>
{
    public async Task<ScoringThroughputDto> Handle(GetScoringThroughputQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Whole-second boundaries keep provider date arithmetic and the returned windows identical.
        var asOf = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        var visibleJobs = await DashboardJobScope.GetVisibleJobsAsync(jobs, user, ct);
        var counts = await throughput.GetHourlyCountsAsync(
            visibleJobs.Select(job => job.Id).ToArray(), asOf.UtcDateTime, ct);
        var start = asOf.AddHours(-24);
        var hours = Enumerable.Range(0, 24)
            .Select(index => new ScoringThroughputBucketDto(
                start.AddHours(index), start.AddHours(index + 1),
                counts.GetValueOrDefault((start.Hour + index) % 24)))
            .ToArray();

        return new ScoringThroughputDto(asOf, hours[^1].Count, hours.Sum(hour => hour.Count), hours);
    }
}
