namespace TalentMatch.Domain.Interfaces;

public sealed record NavigationAuditEntry(
    string Actor,
    string Action,
    string CorrelationId,
    DateTimeOffset RequestedAt);

public interface INavigationAuditRepository
{
    Task<NavigationAuditOutcome> RecordAsync(NavigationAuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed record NavigationAuditOutcome(string ActorObjectId, string Action, string CorrelationId, DateTimeOffset RecordedAt);
