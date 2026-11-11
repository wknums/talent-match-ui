using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Common.Interfaces;

/// <summary>
/// Outcome of a platform submission attempt. Mirrors Stack A
/// (server/services/platform-submitter.ts) so reconciler logic is symmetric.
/// </summary>
public enum PlatformSubmitStatus
{
    Submitted,
    IdempotentHit,
    Transient,
    PermanentFailure,
    Cancelled,
}

public sealed record PlatformSubmitOutcome(
    PlatformSubmitStatus Status,
    string? SubmissionId = null,
    string? PollUrl = null,
    int? RetryAfterMs = null,
    string? Error = null);

public enum PlatformPollStatus
{
    StillRunning,
    Completed,
    Failed,
    Cancelled,
}

public sealed record PlatformPollOutcome(
    PlatformPollStatus Status,
    int? RetryAfterMs = null,
    string? Error = null);

/// <summary>
/// Submits / polls / cancels platform-mode scoring batches against the AWReason
/// platform API (see specs/008-platform-mode-shift/platform-contract.md §4).
/// Only the reconciler hosted service calls this; sequential mode never imports it.
/// </summary>
public interface IPlatformScoringService
{
    Task<PlatformSubmitOutcome> SubmitBatchAsync(ScoringBatch batch, CancellationToken ct = default);
    Task<PlatformPollOutcome> PollBatchAsync(ScoringBatch batch, CancellationToken ct = default);
    Task CancelSubmissionAsync(ScoringBatch batch, CancellationToken ct = default);
}
