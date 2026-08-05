namespace TalentMatch.Domain.Interfaces;

public sealed record NavigationAuditEntry(
    string Actor,
    string Action,
    string CorrelationId,
    DateTimeOffset RequestedAt);

public interface INavigationAuditRepository
{
    Task RecordAsync(NavigationAuditEntry entry, CancellationToken cancellationToken = default);
}
