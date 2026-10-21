using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Domain.Interfaces;

public interface IBulkAuditLedger
{
    Task AppendAsync(
        IReadOnlyCollection<BulkAuditEvent> events,
        CancellationToken cancellationToken = default);
}
