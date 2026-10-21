using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class BulkAuditLedger(AppDbContext context) : IBulkAuditLedger
{
    public async Task AppendAsync(
        IReadOnlyCollection<BulkAuditEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return;
        foreach (var auditEvent in events)
            auditEvent.Validate();
        await context.BulkAuditEvents.AddRangeAsync(events, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}
