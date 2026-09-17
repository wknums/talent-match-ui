using TalentMatch.Application.Jobs.Commands;

namespace TalentMatch.Infrastructure.Services;

public sealed class SequentialScoringOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxParallel { get; init; } = 1;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

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
        };
    }
}
