using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.Common.Interfaces;

public interface IAtomicBulkScoringHandoffRepository
{
    Task<string> HandoffToScoringAsync(
        BulkScoringItem item,
        ProcessingEvent auditEvent,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
