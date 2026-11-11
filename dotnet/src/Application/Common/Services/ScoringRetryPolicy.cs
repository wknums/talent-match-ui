namespace TalentMatch.Application.Common.Services;

public static class ScoringRetryPolicy
{
    public const int MaxAutomaticRetries = 3;

    public static bool CanRetry(int failureCount)
        => failureCount <= MaxAutomaticRetries;

    public static TimeSpan GetBackoff(int failureCount)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Max(0, failureCount - 1)));
        return TimeSpan.FromSeconds(seconds);
    }
}

public sealed class ScoringRetriesExhaustedException : Exception
{
    public ScoringRetriesExhaustedException(int failureCount, Exception innerException)
        : base($"Scoring failed after {failureCount} attempts: {innerException.Message}", innerException)
    {
        FailureCount = failureCount;
    }

    public int FailureCount { get; }
}
