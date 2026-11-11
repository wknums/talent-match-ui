namespace TalentMatch.Application.BulkScoringRequests.Services;

// Transient failures back off exponentially with jitter (R9): base 5 s, cap 5 min, honoring Retry-After.
public static class BulkRetryPolicy
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    // attempt is the number of attempts already made (1 after the first failure).
    public static TimeSpan NextDelay(int attempt, TimeSpan? retryAfter = null, Func<double>? nextRandom = null)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 16);
        var delay = TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks << exponent, MaxDelay.Ticks));
        var jitter = 1 + (((nextRandom ?? Random.Shared.NextDouble)() * 0.4) - 0.2); // ±20 %
        delay = TimeSpan.FromTicks((long)(delay.Ticks * jitter));
        if (retryAfter is { } server && server > delay) delay = server;
        return delay > MaxDelay && retryAfter is null ? MaxDelay : delay;
    }

    public static bool IsExhausted(int attempt, int maxAttempts) => attempt >= maxAttempts;
}
