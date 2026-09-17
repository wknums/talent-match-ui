using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public interface ISequentialScoringQueueRepository
{
    Task<SequentialScoringWork?> TryClaimAsync(
        string owner, DateTime now, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<bool> RenewLeaseAsync(
        string applicationId, string owner, DateTime now, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<bool> FinalizeAsync(
        string applicationId, string owner, Func<CancellationToken, Task> persistResult, CancellationToken ct = default);
    Task FailAsync(
        string applicationId, string owner, string error, int failureCount, CancellationToken ct = default);
    Task RecoverExpiredAsync(DateTime now, CancellationToken ct = default);
    Task<bool> RetryAsync(string itemId, CancellationToken ct = default);
}
