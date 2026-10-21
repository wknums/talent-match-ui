using System.Text.RegularExpressions;
using FluentAssertions;

namespace TalentMatch.Web.Tests;

public sealed class BulkScoringOpenApiContractTests
{
    private static readonly string[] RequiredAuditFields =
    [
        "auditEventId",
        "eventType",
        "correlationId",
        "actorType",
        "actorId",
        "occurredAt",
        "subjectType",
        "subjectId",
        "safeFacts",
        "schemaVersion",
    ];

    private static readonly string[] CompleteAuditEventRegistry =
    [
        "source_connection_created",
        "source_connection_completed",
        "source_connection_failed",
        "source_connection_expired",
        "source_connection_consumed",
        "preview_draft_created",
        "preview_discovery_committed",
        "preview_draft_abandoned",
        "bulk_request_confirmed",
        "bulk_request_cancellation_requested",
        "bulk_request_retry_created",
        "bulk_item_discovered",
        "bulk_item_claimed",
        "bulk_item_lease_renewed",
        "bulk_item_lease_recovered",
        "bulk_item_lease_released",
        "bulk_item_import_started",
        "bulk_item_source_metadata_recheck_succeeded",
        "bulk_item_source_metadata_recheck_failed",
        "bulk_original_content_staged",
        "bulk_converted_content_staged",
        "bulk_item_fingerprint_recorded",
        "bulk_item_duplicate_claimed",
        "bulk_item_duplicate_skipped",
        "bulk_item_conversion_started",
        "bulk_item_conversion_completed",
        "bulk_item_conversion_failed",
        "bulk_converted_output_quarantined",
        "bulk_converted_output_removed",
        "bulk_item_application_created",
        "bulk_item_document_created",
        "bulk_item_scoring_handoff_queued",
        "bulk_item_scoring_started",
        "bulk_item_completed",
        "bulk_item_failed",
        "bulk_item_skipped",
        "bulk_item_cancelled",
        "bulk_retry_item_created",
        "bulk_request_counts_reconciled",
        "bulk_request_terminal_finalized",
        "bulk_notification_created",
        "bulk_notification_acknowledged",
        "request_authorization_established",
        "request_authorization_used",
        "request_authorization_lapsed",
        "request_authorization_discarded",
        "bulk_original_content_deleted",
        "bulk_converted_content_deleted",
        "bulk_transient_content_deleted",
        "bulk_item_reconciliation_corrected",
        "bulk_request_reconciliation_corrected",
        "bulk_content_cleanup_reconciliation_pending",
        "bulk_content_cleanup_reconciliation_completed",
        "bulk_settings_changed",
        "bulk_job_cleanup_completed",
        "bulk_job_cleanup_pending",
    ];

    [Fact]
    public void Contract_is_OpenApi_3_1_and_freezes_the_complete_v3_1_surface()
    {
        var contract = ReadContract();

        contract.Should().StartWith("openapi: 3.1.0");
        contract.Should().Contain("version: 3.1.0");
        contract.Should().Contain("x-runtime-status: planned-until-old-runtime-removal");
        contract.Should().Contain("enum: [sharepoint, onedrive_business]");
        contract.Should().Contain("supportedCloud: { const: microsoftCommercial }");

        string[] operations =
        [
            "beginSourceConnection",
            "completeSourceConnectionCallback",
            "completeSourceAdminConsentCallback",
            "getSourceConnection",
            "abandonSourceConnection",
            "previewBulkScoringRequest",
            "discoverMoreBulkScoringRequest",
            "listBulkScoringDraftItems",
            "confirmBulkScoringRequest",
            "listJobBulkScoringRequests",
            "getBulkScoringRequest",
            "listBulkScoringRequestItems",
            "cancelBulkScoringRequest",
            "beginRetrySourceConnection",
            "listBulkScoringNotifications",
            "acknowledgeBulkScoringRequest",
            "downloadBulkScoringOriginal",
            "getBulkScoringSettings",
            "updateBulkScoringSettings",
        ];
        operations.Should().AllSatisfy(operation => contract.Should().Contain($"operationId: {operation}"));

        contract.Should().Contain("reviewSnapshotVersion");
        contract.Should().Contain("nextReviewCursor");
        contract.Should().Contain("nextCursor");
        contract.Should().Contain("maxItems: 100");
        contract.Should().Contain("maxFilesPerRequest");
        contract.Should().Contain("maximum: 100000");
        contract.Should().Contain("maxTotalBytes");
        contract.Should().Contain("maximum: 1099511627776");
        contract.Should().Contain("capturedScoringConfigVersionId");
        contract.Should().Contain("currentScoringConfigVersionId");
        contract.Should().Contain("configChangedSinceAcceptance");
        contract.Should().Contain("expectedConcurrencyVersion");
    }

    [Fact]
    public void Contract_freezes_raster_actor_notification_retention_and_timing_semantics()
    {
        var contract = ReadContract();

        contract.Should().Contain("configured output type and PDF magic/signature validation");
        contract.Should().Contain("exact page count and order");
        contract.Should().Contain("source/output dimensions within one point");
        contract.Should().Contain("every page pair SSIM at least 0.95");
        contract.Should().Contain("blank and cropped output rejected without original fallback");
        contract.Should().Contain("hasRetainedOriginal");
        contract.Should().Contain("original");
        contract.Should().Contain("converted");

        contract.Should().Contain("recorded submit actor identifier");
        contract.Should().Contain("recorded cancel actor identifier");
        contract.Should().NotContain("username", "recorded actor IDs are the only fallback");

        var notification = SchemaSlice(contract, "BulkScoringNotification", "BulkScoringItem");
        notification.Should().Contain("additionalProperties: false");
        notification.Should().Contain("requestId");
        notification.Should().Contain("outcome");
        notification.Should().Contain("counts");
        notification.Should().Contain("requestDetailPath");
        notification.Should().Contain("terminalAt");
        notification.Should().NotContain("itemName:");
        notification.Should().NotContain("items:");
        notification.Should().NotContain("folderName:");
        notification.Should().NotContain("sourceWebUrl:");

        contract.Should().Contain("one 60-second budget");
        contract.Should().Contain("job-lifetime request and item outcomes");
        contract.Should().Contain("SQLite and Azure SQL cascade plus staged-content cleanup");
        contract.Should().Contain("immutable audit retention");
    }

    [Fact]
    public void Immutable_audit_extension_enumerates_every_event_and_Principle_I_invariant()
    {
        var contract = ReadContract();
        var audit = Slice(contract, "x-immutable-audit-contract:", "servers:");

        RequiredAuditFields.Should().AllSatisfy(field => audit.Should().Contain($"- {field}"));
        CompleteAuditEventRegistry.Should().OnlyHaveUniqueItems();
        CompleteAuditEventRegistry.Should().AllSatisfy(eventType => audit.Should().Contain(eventType));

        audit.Should().Contain("actorType: user");
        audit.Should().Contain("actorType: service");
        audit.Should().Contain("initiatedByActorId");
        audit.Should().Contain("SQLite or Azure SQL transaction");
        audit.Should().Contain("durable handoff or outbox");
        audit.Should().Contain("durable intent, outcome, or reconciliation correction");
        audit.Should().Contain("append-only and detached");
        audit.Should().Contain("providerParity: [stack-a-sqlite, stack-a-azure-sql, stack-b-sqlite, stack-b-azure-sql]");
        audit.Should().Contain("raw source account identity or email");
        audit.Should().Contain("document content or fingerprints");
        audit.Should().Contain("authorization codes, tokens, verifiers, caches, keys, or provider payloads");
        audit.Should().Contain("environment-specific identifiers or secrets");
    }

    [Fact]
    public void Public_schemas_reject_legacy_authorization_fields_and_sensitive_audit_payloads()
    {
        var contract = ReadContract();

        Regex.IsMatch(
                contract,
                @"(?mi)^\s*(accessModel|owningStack|allowedHosts|permittedRoots|sourceTenantAllowList)\s*:")
            .Should().BeFalse();
        contract.Should().NotContain("enum: [same_tenant, cross_tenant]");
        contract.Should().NotContain("enum: [same-tenant, cross-tenant]");

        var audit = Slice(contract, "x-immutable-audit-contract:", "servers:");
        string[] prohibited =
        [
            "source, folder, or item names and URLs",
            "raw source account identity or email",
            "document content or fingerprints",
            "authorization codes, tokens, verifiers, caches, keys, or provider payloads",
            "environment-specific identifiers or secrets",
        ];
        prohibited.Should().AllSatisfy(value => audit.Should().Contain(value));
    }

    private static string ReadContract()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName,
                "specs",
                "001-sharepoint-bulk-scoring",
                "contracts",
                "openapi.yaml");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException("Bulk-scoring OpenAPI contract was not found above " + AppContext.BaseDirectory);
    }

    private static string SchemaSlice(string contract, string startSchema, string nextSchema) =>
        Slice(contract, $"    {startSchema}:", $"    {nextSchema}:");

    private static string Slice(string value, string startMarker, string endMarker)
    {
        var start = value.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = value.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return value[start..end];
    }
}
