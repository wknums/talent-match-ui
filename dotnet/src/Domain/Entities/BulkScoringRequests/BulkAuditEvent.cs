using System.Text.Json;

namespace TalentMatch.Domain.Entities.BulkScoringRequests;

/// <summary>
/// Detached append-only audit row for bulk-source state changes. It deliberately has no
/// navigation to jobs, requests, items, content, or authorization records so operational
/// cascades cannot delete audit history.
/// </summary>
public sealed class BulkAuditEvent
{
    public string AuditEventId { get; init; } = Guid.NewGuid().ToString();
    public string EventType { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public BulkAuditActorType ActorType { get; init; }
    public string ActorId { get; init; } = string.Empty;
    public string? InitiatedByActorId { get; init; }
    public DateTime OccurredAt { get; init; }
    public string SubjectType { get; init; } = string.Empty;
    public string SubjectId { get; init; } = string.Empty;
    public string SafeFactsJson { get; init; } = "{}";
    public int SchemaVersion { get; init; } = 1;
    public BulkAuditBoundary Boundary { get; init; } = BulkAuditBoundary.Transactional;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EventType) || !BulkAuditEventTypes.All.Contains(EventType))
            throw new InvalidOperationException("A closed bulk audit event type is required.");
        if (!Guid.TryParse(CorrelationId, out _) || string.IsNullOrWhiteSpace(ActorId)
            || string.IsNullOrWhiteSpace(SubjectType) || string.IsNullOrWhiteSpace(SubjectId))
            throw new InvalidOperationException("Correlation, actor, and opaque subject bindings are required.");
        if (ActorType == BulkAuditActorType.User && InitiatedByActorId is not null)
            throw new InvalidOperationException("A user audit actor cannot have an initiating user.");
        if (OccurredAt.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("Bulk audit timestamps must be UTC.");
        if (SchemaVersion <= 0 || string.IsNullOrWhiteSpace(SafeFactsJson))
            throw new InvalidOperationException("Versioned safe facts are required.");
        ValidateSafeFacts();
    }

    private void ValidateSafeFacts()
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(SafeFactsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Audit safe facts must be valid JSON.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("factVersion", out var version)
                || !version.TryGetInt32(out var factVersion)
                || factVersion <= 0)
                throw new InvalidOperationException("Audit safe facts require a positive factVersion.");

            RejectProhibitedProperties(document.RootElement);
        }
    }

    private static void RejectProhibitedProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectProhibitedProperties(item);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        string[] prohibited =
        [
            "url", "foldername", "itemname", "filename", "path", "email", "account",
            "authorizationcode", "token", "verifier", "cache", "key", "fingerprint",
            "contentbase64", "providerpayload", "resourceid", "secret",
        ];
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            if (prohibited.Any(name.Contains))
                throw new InvalidOperationException("Audit safe facts contain prohibited source data.");
            RejectProhibitedProperties(property.Value);
        }
    }
}

/// <summary>Closed v3.1 event registry mirrored by OpenAPI.</summary>
public static class BulkAuditEventTypes
{
    public const string SourceConnectionCreated = "source_connection_created";
    public const string SourceConnectionCompleted = "source_connection_completed";
    public const string SourceConnectionFailed = "source_connection_failed";
    public const string SourceConnectionExpired = "source_connection_expired";
    public const string SourceConnectionConsumed = "source_connection_consumed";
    public const string PreviewDraftCreated = "preview_draft_created";
    public const string PreviewDiscoveryCommitted = "preview_discovery_committed";
    public const string PreviewDraftAbandoned = "preview_draft_abandoned";
    public const string BulkItemDiscovered = "bulk_item_discovered";
    public const string BulkRequestConfirmed = "bulk_request_confirmed";
    public const string BulkRequestCancellationRequested = "bulk_request_cancellation_requested";
    public const string BulkRequestRetryCreated = "bulk_request_retry_created";
    public const string BulkItemClaimed = "bulk_item_claimed";
    public const string BulkItemLeaseRenewed = "bulk_item_lease_renewed";
    public const string BulkItemLeaseRecovered = "bulk_item_lease_recovered";
    public const string BulkItemLeaseReleased = "bulk_item_lease_released";
    public const string BulkItemImportStarted = "bulk_item_import_started";
    public const string BulkItemSourceMetadataRecheckSucceeded = "bulk_item_source_metadata_recheck_succeeded";
    public const string BulkItemSourceMetadataRecheckFailed = "bulk_item_source_metadata_recheck_failed";
    public const string BulkOriginalContentStaged = "bulk_original_content_staged";
    public const string BulkConvertedContentStaged = "bulk_converted_content_staged";
    public const string BulkItemFingerprintRecorded = "bulk_item_fingerprint_recorded";
    public const string BulkItemDuplicateClaimed = "bulk_item_duplicate_claimed";
    public const string BulkItemDuplicateSkipped = "bulk_item_duplicate_skipped";
    public const string BulkItemConversionStarted = "bulk_item_conversion_started";
    public const string BulkItemConversionCompleted = "bulk_item_conversion_completed";
    public const string BulkItemConversionFailed = "bulk_item_conversion_failed";
    public const string BulkConvertedOutputQuarantined = "bulk_converted_output_quarantined";
    public const string BulkConvertedOutputRemoved = "bulk_converted_output_removed";
    public const string BulkItemApplicationCreated = "bulk_item_application_created";
    public const string BulkItemDocumentCreated = "bulk_item_document_created";
    public const string BulkItemScoringHandoffQueued = "bulk_item_scoring_handoff_queued";
    public const string BulkItemScoringStarted = "bulk_item_scoring_started";
    public const string BulkItemCompleted = "bulk_item_completed";
    public const string BulkItemFailed = "bulk_item_failed";
    public const string BulkItemSkipped = "bulk_item_skipped";
    public const string BulkItemCancelled = "bulk_item_cancelled";
    public const string BulkRetryItemCreated = "bulk_retry_item_created";
    public const string BulkRequestCountsReconciled = "bulk_request_counts_reconciled";
    public const string BulkRequestTerminalFinalized = "bulk_request_terminal_finalized";
    public const string BulkNotificationCreated = "bulk_notification_created";
    public const string BulkNotificationAcknowledged = "bulk_notification_acknowledged";
    public const string RequestAuthorizationEstablished = "request_authorization_established";
    public const string RequestAuthorizationUsed = "request_authorization_used";
    public const string RequestAuthorizationLapsed = "request_authorization_lapsed";
    public const string RequestAuthorizationDiscarded = "request_authorization_discarded";
    public const string BulkOriginalContentDeleted = "bulk_original_content_deleted";
    public const string BulkConvertedContentDeleted = "bulk_converted_content_deleted";
    public const string BulkTransientContentDeleted = "bulk_transient_content_deleted";
    public const string BulkItemReconciliationCorrected = "bulk_item_reconciliation_corrected";
    public const string BulkRequestReconciliationCorrected = "bulk_request_reconciliation_corrected";
    public const string BulkContentCleanupReconciliationPending = "bulk_content_cleanup_reconciliation_pending";
    public const string BulkContentCleanupReconciliationCompleted = "bulk_content_cleanup_reconciliation_completed";
    public const string BulkSettingsChanged = "bulk_settings_changed";
    public const string BulkJobCleanupCompleted = "bulk_job_cleanup_completed";
    public const string BulkJobCleanupPending = "bulk_job_cleanup_pending";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        typeof(BulkAuditEventTypes).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
        StringComparer.Ordinal);
}
