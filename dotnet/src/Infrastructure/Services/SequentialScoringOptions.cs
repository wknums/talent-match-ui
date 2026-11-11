using TalentMatch.Application.Jobs.Commands;

namespace TalentMatch.Infrastructure.Services;

public sealed class SequentialScoringOptions
{
    public const string LeaseSecondsVariable = "AWR_SCORING_LEASE_SECONDS";
    public const int DefaultLeaseSeconds = 240;
    public const int MinLeaseSeconds = 60;
    public const int MaxLeaseSeconds = 1800;

    public bool Enabled { get; init; } = true;
    public int MaxParallel { get; init; } = 1;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(DefaultLeaseSeconds);

    // A failed lease renewal is retried this many times, waiting RenewRetryDelay and doubling up to RenewRetryMaxDelay.
    // If every attempt fails the pool keeps trying at the next heartbeat for as long as the lease is still valid.
    public int RenewAttempts { get; init; } = 5;
    public TimeSpan RenewRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RenewRetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    public static SequentialScoringOptions FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("AWR_MAX_PARALLEL");
        var maxParallel = 1;
        if (!string.IsNullOrWhiteSpace(configured)
            && (!int.TryParse(configured, out maxParallel) || maxParallel < 1))
            throw new InvalidOperationException("AWR_MAX_PARALLEL must be a positive integer.");

        return new SequentialScoringOptions
        {
            Enabled = ProcessJobCommandHandler.ResolveScoringMode() == "sequential"
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")),
            MaxParallel = maxParallel,
            LeaseDuration = ResolveLeaseDuration(Environment.GetEnvironmentVariable(LeaseSecondsVariable)),
        };
    }

    public static TimeSpan ResolveLeaseDuration(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return TimeSpan.FromSeconds(DefaultLeaseSeconds);

        if (!int.TryParse(configured, out var seconds) || seconds < MinLeaseSeconds || seconds > MaxLeaseSeconds)
            throw new InvalidOperationException(
                $"{LeaseSecondsVariable} must be a whole number of seconds from {MinLeaseSeconds} to {MaxLeaseSeconds}.");

        return TimeSpan.FromSeconds(seconds);
    }
}
