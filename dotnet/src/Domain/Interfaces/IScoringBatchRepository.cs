using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public interface IScoringBatchRepository
{
    Task<ScoringBatch> CreateAsync(ScoringBatch batch, CancellationToken ct = default);
    Task<ScoringBatch?> GetByIdAsync(string batchId, CancellationToken ct = default);
    Task<IReadOnlyList<ScoringBatch>> ListByJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Returns candidate batches whose lease is free or expired and whose
    /// NextPollAt is in the past. Does NOT acquire the lease.
    /// </summary>
    Task<IReadOnlyList<ScoringBatch>> FindDueCandidatesAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Conditional UPDATE that sets LeaseOwner+LeasedUntil only if the row's
    /// lease is unowned or expired. Returns true if we won the race.
    /// </summary>
    Task<bool> AcquireLeaseAsync(string batchId, string owner, int leaseSeconds, CancellationToken ct = default);

    Task ReleaseLeaseAsync(string batchId, CancellationToken ct = default);
    Task MarkSubmittedAsync(string batchId, string submissionId, string? pollUrl, DateTime nextPollAt, CancellationToken ct = default);
    Task SetNextPollAtAsync(string batchId, DateTime nextPollAt, CancellationToken ct = default);
    Task MarkCompletedAsync(string batchId, string resultJson, CancellationToken ct = default);
    Task MarkFailedAsync(string batchId, string error, CancellationToken ct = default);
    Task MarkCancelledAsync(string batchId, CancellationToken ct = default);
    Task IncrementAttemptAsync(string batchId, string error, DateTime nextPollAt, CancellationToken ct = default);
    Task<int> RequestCancelByJobAsync(string jobId, CancellationToken ct = default);

    // Progress
    Task InitProgressAsync(string jobId, int totalApps, int batchesPending, CancellationToken ct = default);
    Task<ScoringJobProgress?> GetProgressAsync(string jobId, CancellationToken ct = default);
    Task RequestCancelProgressAsync(string jobId, CancellationToken ct = default);
    Task ApplyTransitionAsync(
        string jobId,
        string? fromStatus,
        string? toStatus,
        int appsCompletedDelta,
        int appsFailedDelta,
        CancellationToken ct = default);
}
