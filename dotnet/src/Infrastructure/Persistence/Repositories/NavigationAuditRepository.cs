using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class NavigationAuditRepository(AppDbContext context) : INavigationAuditRepository
{
    public async Task<NavigationAuditOutcome> RecordAsync(NavigationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        // Navigation audits are append-only: a replayed correlation id must never rewrite the original outcome.
        var alreadyRecorded = await context.ProcessingEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.CorrelationId == entry.CorrelationId
                && item.EventType == ProcessingEvent.AuthorizationActions.NavigationShellChanged, cancellationToken);
        if (alreadyRecorded is not null)
        {
            if (alreadyRecorded.Actor != entry.Actor)
                throw new InvalidOperationException("The correlation belongs to another actor.");
            using var details = JsonDocument.Parse(alreadyRecorded.PayloadJson);
            return new(alreadyRecorded.Actor, details.RootElement.GetProperty("action").GetString()!,
                alreadyRecorded.CorrelationId!, new DateTimeOffset(DateTime.SpecifyKind(alreadyRecorded.Timestamp, DateTimeKind.Utc)));
        }

        var recordedAt = DateTime.UtcNow;
        context.ProcessingEvents.Add(new ProcessingEvent
        {
            Actor = entry.Actor,
            EventType = ProcessingEvent.AuthorizationActions.NavigationShellChanged,
            EntityType = "navigation_shell",
            EntityId = entry.Actor,
            CorrelationId = entry.CorrelationId,
            Timestamp = recordedAt,
            PayloadJson = JsonSerializer.Serialize(new
            {
                action = entry.Action,
                result = "succeeded",
                requestedAt = entry.RequestedAt.UtcDateTime,
            }),
        });

        await context.SaveChangesAsync(cancellationToken);
        return new(entry.Actor, entry.Action, entry.CorrelationId, new DateTimeOffset(recordedAt));
    }
}
