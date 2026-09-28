using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class NavigationAuditRepository(AppDbContext context) : INavigationAuditRepository
{
    public async Task RecordAsync(NavigationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        // Navigation audits are append-only: a replayed correlation id must never rewrite the original outcome.
        var alreadyRecorded = await context.ProcessingEvents
            .AsNoTracking()
            .AnyAsync(item => item.CorrelationId == entry.CorrelationId, cancellationToken);
        if (alreadyRecorded)
            return;

        context.ProcessingEvents.Add(new ProcessingEvent
        {
            Actor = entry.Actor,
            EventType = ProcessingEvent.AuthorizationActions.NavigationShellChanged,
            EntityType = "navigation_shell",
            EntityId = entry.Actor,
            CorrelationId = entry.CorrelationId,
            Timestamp = DateTime.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new
            {
                action = entry.Action,
                result = "succeeded",
                requestedAt = entry.RequestedAt.UtcDateTime,
            }),
        });

        await context.SaveChangesAsync(cancellationToken);
    }
}
