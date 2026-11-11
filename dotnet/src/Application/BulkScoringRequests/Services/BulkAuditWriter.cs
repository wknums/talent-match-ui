using System.Text.Json;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests;

public sealed class BulkAuditWriter(
    IBulkImportSettingsRepository? settingsRepository = null,
    IBulkAuditLedger? ledger = null)
{
    public Task AppendAsync(
        IReadOnlyCollection<BulkAuditEvent> events,
        CancellationToken cancellationToken = default) =>
        (ledger ?? throw new InvalidOperationException("The bulk audit ledger is not configured."))
            .AppendAsync(events, cancellationToken);

    public static BulkAuditEvent User(
        string eventType,
        string actorId,
        string correlationId,
        DateTime occurredAtUtc,
        string subjectType,
        string subjectId,
        object? safeFacts = null,
        BulkAuditBoundary boundary = BulkAuditBoundary.Transactional) =>
        Create(
            eventType,
            BulkAuditActorType.User,
            actorId,
            null,
            correlationId,
            occurredAtUtc,
            subjectType,
            subjectId,
            safeFacts,
            boundary);

    public static BulkAuditEvent Service(
        string eventType,
        string serviceActorId,
        string? initiatedByActorId,
        string correlationId,
        DateTime occurredAtUtc,
        string subjectType,
        string subjectId,
        object? safeFacts = null,
        BulkAuditBoundary boundary = BulkAuditBoundary.Transactional) =>
        Create(
            eventType,
            BulkAuditActorType.Service,
            serviceActorId,
            initiatedByActorId,
            correlationId,
            occurredAtUtc,
            subjectType,
            subjectId,
            safeFacts,
            boundary);

    public Task<BulkImportSettings> SaveSettingsChangedAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        int oldMaxFilesPerRequest,
        int oldConcurrencyVersion,
        string administratorActorId,
        string correlationId,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var audit = User(
            BulkAuditEventTypes.BulkSettingsChanged,
            administratorActorId,
            correlationId,
            occurredAtUtc,
            nameof(BulkImportSettings),
            BulkImportSettings.SingletonId.ToString(),
            new
            {
                old = new
                {
                    maxFilesPerRequest = oldMaxFilesPerRequest,
                    concurrencyVersion = oldConcurrencyVersion,
                },
                @new = new
                {
                    maxFilesPerRequest = settings.MaxFilesPerRequest,
                    concurrencyVersion = expectedConcurrencyVersion + 1,
                },
            });

        return (settingsRepository
                ?? throw new InvalidOperationException("The bulk settings repository is not configured."))
            .SaveWithAuditAsync(
            settings,
            expectedConcurrencyVersion,
            new ProcessingEvent
            {
                Id = audit.AuditEventId,
                Actor = audit.ActorId,
                EventType = audit.EventType,
                EntityType = audit.SubjectType,
                EntityId = audit.SubjectId,
                CorrelationId = audit.CorrelationId,
                Timestamp = audit.OccurredAt,
                PayloadJson = audit.SafeFactsJson,
            },
            audit,
            cancellationToken);
    }

    private static BulkAuditEvent Create(
        string eventType,
        BulkAuditActorType actorType,
        string actorId,
        string? initiatedByActorId,
        string correlationId,
        DateTime occurredAtUtc,
        string subjectType,
        string subjectId,
        object? safeFacts,
        BulkAuditBoundary boundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (!Guid.TryParse(correlationId, out _))
            throw new ArgumentException("A valid correlation identifier is required.", nameof(correlationId));

        var facts = new Dictionary<string, object?>
        {
            ["factVersion"] = 1,
        };
        if (safeFacts is not null)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(safeFacts));
            foreach (var property in document.RootElement.EnumerateObject())
                facts[property.Name] = property.Value.Clone();
        }
        facts["factVersion"] = 1;

        var audit = new BulkAuditEvent
        {
            EventType = eventType,
            CorrelationId = correlationId,
            ActorType = actorType,
            ActorId = actorId,
            InitiatedByActorId = string.IsNullOrWhiteSpace(initiatedByActorId) ? null : initiatedByActorId,
            OccurredAt = Utc(occurredAtUtc),
            SubjectType = subjectType,
            SubjectId = subjectId,
            SafeFactsJson = JsonSerializer.Serialize(facts),
            SchemaVersion = 1,
            Boundary = boundary,
        };
        audit.Validate();
        return audit;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
