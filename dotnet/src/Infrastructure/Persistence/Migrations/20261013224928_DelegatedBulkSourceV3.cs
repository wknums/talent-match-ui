using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DelegatedBulkSourceV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // V3 deliberately refuses to translate any active app-only/OBO authorization into a
            // delegated connector cache. Operators must drain or explicitly discard old work first.
            if (ActiveProvider.Contains("SqlServer", StringComparison.Ordinal))
            {
                migrationBuilder.Sql(
                    """
                    IF EXISTS (
                        SELECT 1
                        FROM [talentmatch].[BulkScoringRequests]
                        WHERE [Status] IN ('draft', 'active')
                    )
                    THROW 51013, 'delegated_v3_old_active_work_requires_drain_or_discard', 1;
                    """);
            }
            else
            {
                migrationBuilder.Sql(
                    """
                    CREATE TEMP TABLE "__DelegatedV3ActiveWorkGuard" (
                        "ActiveCount" INTEGER NOT NULL CHECK ("ActiveCount" = 0)
                    );
                    INSERT INTO "__DelegatedV3ActiveWorkGuard" ("ActiveCount")
                    SELECT COUNT(*) FROM "BulkScoringRequests" WHERE "Status" IN ('draft', 'active');
                    DROP TABLE "__DelegatedV3ActiveWorkGuard";
                    """);
            }

            migrationBuilder.DropIndex(
                name: "IX_BulkScoringRequests_Status_ExpiresAt",
                table: "BulkScoringRequests");

            migrationBuilder.DropIndex(
                name: "IX_BulkScoringRequests_Status_OwningStack",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringRequests_AccessModel",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringRequests_OwningStack",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringRequests_SourceKind",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringItems_Reason",
                table: "BulkScoringItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkRequestAuthorizations_Status",
                table: "BulkRequestAuthorizations");

            migrationBuilder.DropColumn(
                name: "AccessModel",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "LimitsJson",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "OwningStack",
                table: "BulkScoringRequests");

            migrationBuilder.RenameColumn(
                name: "JobConfigVersionId",
                table: "BulkScoringRequests",
                newName: "ConnectionId");

            migrationBuilder.RenameColumn(
                name: "PageCount",
                table: "BulkScoringItems",
                newName: "RasterDpiUsed");

            migrationBuilder.RenameColumn(
                name: "RecruiterActorId",
                table: "BulkRequestAuthorizations",
                newName: "SourceAccountBindingHash");

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedByActorId",
                table: "BulkScoringRequests",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CapturedScoringConfigVersionId",
                table: "BulkScoringRequests",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryStepMaxEntries",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryStepMaxFiles",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryStepMaxSeconds",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DraftReviewVersion",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "IncludedBytes",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "IncludedItemCount",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "MaxBytesPerFile",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilesPerRequest",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "MaxTotalBytes",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "RasterDpi",
                table: "BulkScoringRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceAccountBindingHash",
                table: "BulkScoringRequests",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ConvertedPageCount",
                table: "BulkScoringItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumPageSsim",
                table: "BulkScoringItems",
                type: "TEXT",
                precision: 6,
                scale: 5,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalPageCount",
                table: "BulkScoringItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RasterFidelityValidatedAt",
                table: "BulkScoringItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScoringConfigVersionId",
                table: "BulkScoringItems",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CleanupReasonCode",
                table: "BulkScoringItemContents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "BulkScoringItemContents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletionRequestedAt",
                table: "BulkScoringItemContents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductActorId",
                table: "BulkRequestAuthorizations",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceDriveId",
                table: "BulkRequestAuthorizations",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceFolderItemId",
                table: "BulkRequestAuthorizations",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceTenantId",
                table: "BulkRequestAuthorizations",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ConfigurationVersion",
                table: "BulkImportSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "BulkSourceAdmissionEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SubjectHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkSourceAdmissionEvents", x => x.Id);
                    table.CheckConstraint("CK_BulkSourceAdmissionEvents_Expiry", "ExpiresAt > OccurredAt");
                    table.CheckConstraint("CK_BulkSourceAdmissionEvents_Kind", "Kind IN ('connection_actor', 'connection_ip', 'callback_ip')");
                });

            migrationBuilder.CreateTable(
                name: "BulkSourceConnections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProductActorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    JobId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CanonicalUrlHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PendingFolderUrlEncrypted = table.Column<byte[]>(type: "BLOB", nullable: true),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PkceVerifierEncrypted = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RedirectAfterPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SourceTenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceAccountBindingHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SourceAccountDisplay = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    SourceCloud = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    SourceDriveId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceFolderItemId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceFolderName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SourceFolderPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SourceWebUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ProtectedPayload = table.Column<byte[]>(type: "BLOB", nullable: true),
                    WrappedDataKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    KeyVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ObtainedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedByRequestId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    DiscardedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ConcurrencyVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkSourceConnections", x => x.Id);
                    table.CheckConstraint("CK_BulkSourceConnections_Consumed", "(Status <> 'consumed') OR (ConsumedByRequestId IS NOT NULL AND PendingFolderUrlEncrypted IS NULL AND StateHash IS NULL AND PkceVerifierEncrypted IS NULL AND ProtectedPayload IS NULL AND WrappedDataKey IS NULL)");
                    table.CheckConstraint("CK_BulkSourceConnections_Expiry", "ExpiresAt >= LastActivityAt AND ExpiresAt >= ObtainedAt");
                    table.CheckConstraint("CK_BulkSourceConnections_SourceKind", "SourceKind IS NULL OR SourceKind IN ('sharepoint', 'onedrive_business')");
                    table.CheckConstraint("CK_BulkSourceConnections_Status", "Status IN ('authorizing', 'authorized', 'verified', 'consumed', 'expired', 'abandoned', 'failed')");
                    table.ForeignKey(
                        name: "FK_BulkSourceConnections_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_ConnectionId",
                table: "BulkScoringRequests",
                column: "ConnectionId",
                unique: true,
                filter: "[ConnectionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_Status_TerminalAt",
                table: "BulkScoringRequests",
                columns: new[] { "Status", "TerminalAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringRequests_Acknowledgment",
                table: "BulkScoringRequests",
                sql: "(AcknowledgedAt IS NULL AND AcknowledgedByActorId IS NULL) OR (AcknowledgedAt IS NOT NULL AND AcknowledgedByActorId = OwnerActorId)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringRequests_FrozenLimits",
                table: "BulkScoringRequests",
                sql: "MaxFilesPerRequest BETWEEN 1 AND 100000 AND MaxBytesPerFile > 0 AND MaxTotalBytes BETWEEN 1048576 AND 1099511627776 AND DiscoveryStepMaxFiles BETWEEN 1 AND 1000 AND DiscoveryStepMaxEntries > 0 AND DiscoveryStepMaxSeconds > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringRequests_IncludedLimits",
                table: "BulkScoringRequests",
                sql: "IncludedItemCount >= 0 AND IncludedItemCount <= MaxFilesPerRequest AND IncludedBytes >= 0 AND IncludedBytes <= MaxTotalBytes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringRequests_SourceKind",
                table: "BulkScoringRequests",
                sql: "SourceKind IN ('sharepoint', 'onedrive_business')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringItems_Reason",
                table: "BulkScoringItems",
                sql: "ReasonCode IS NULL OR ReasonCode IN ('unsupported_type', 'file_too_large', 'limit_exceeded', 'empty_file', 'not_convertible', 'access_denied', 'authorization_lapsed', 'source_missing', 'source_changed', 'duplicate_in_request', 'duplicate_in_job', 'conversion_failed', 'conversion_timeout', 'converted_type_invalid', 'converted_magic_invalid', 'converted_document_invalid', 'converted_too_large', 'conversion_page_mismatch', 'conversion_page_invalid', 'conversion_dimension_mismatch', 'conversion_ssim_below_threshold', 'conversion_blank_page', 'conversion_cropped_page', 'scoring_failed', 'cancelled_by_user', 'transient_exhausted')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkRequestAuthorizations_Status",
                table: "BulkRequestAuthorizations",
                sql: "Status IN ('active', 'lapsed', 'revoked', 'accessLost', 'discarded', 'unavailableHistorical')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkImportSettings_MaxFilesV3",
                table: "BulkImportSettings",
                sql: "MaxFilesPerRequest <= 100000");

            migrationBuilder.CreateIndex(
                name: "IX_BulkSourceAdmissionEvents_ExpiresAt",
                table: "BulkSourceAdmissionEvents",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_BulkSourceAdmissionEvents_Kind_SubjectHash_ExpiresAt",
                table: "BulkSourceAdmissionEvents",
                columns: new[] { "Kind", "SubjectHash", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkSourceConnections_JobId",
                table: "BulkSourceConnections",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkSourceConnections_ProductActorId_JobId_Status_ExpiresAt",
                table: "BulkSourceConnections",
                columns: new[] { "ProductActorId", "JobId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkSourceConnections_StateHash",
                table: "BulkSourceConnections",
                column: "StateHash",
                unique: true,
                filter: "[StateHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BulkSourceAdmissionEvents");

            migrationBuilder.DropTable(
                name: "BulkSourceConnections");

            migrationBuilder.DropIndex(
                name: "IX_BulkScoringRequests_ConnectionId",
                table: "BulkScoringRequests");

            migrationBuilder.DropIndex(
                name: "IX_BulkScoringRequests_Status_TerminalAt",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringRequests_Acknowledgment",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringRequests_FrozenLimits",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringRequests_IncludedLimits",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringRequests_SourceKind",
                table: "BulkScoringRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringItems_Reason",
                table: "BulkScoringItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkRequestAuthorizations_Status",
                table: "BulkRequestAuthorizations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkImportSettings_MaxFilesV3",
                table: "BulkImportSettings");

            migrationBuilder.DropColumn(
                name: "AcknowledgedByActorId",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "CapturedScoringConfigVersionId",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "DiscoveryStepMaxEntries",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "DiscoveryStepMaxFiles",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "DiscoveryStepMaxSeconds",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "DraftReviewVersion",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "IncludedBytes",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "IncludedItemCount",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "MaxBytesPerFile",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "MaxFilesPerRequest",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "MaxTotalBytes",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "RasterDpi",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "SourceAccountBindingHash",
                table: "BulkScoringRequests");

            migrationBuilder.DropColumn(
                name: "ConvertedPageCount",
                table: "BulkScoringItems");

            migrationBuilder.DropColumn(
                name: "MinimumPageSsim",
                table: "BulkScoringItems");

            migrationBuilder.DropColumn(
                name: "OriginalPageCount",
                table: "BulkScoringItems");

            migrationBuilder.DropColumn(
                name: "RasterFidelityValidatedAt",
                table: "BulkScoringItems");

            migrationBuilder.DropColumn(
                name: "ScoringConfigVersionId",
                table: "BulkScoringItems");

            migrationBuilder.DropColumn(
                name: "CleanupReasonCode",
                table: "BulkScoringItemContents");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "BulkScoringItemContents");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedAt",
                table: "BulkScoringItemContents");

            migrationBuilder.DropColumn(
                name: "ProductActorId",
                table: "BulkRequestAuthorizations");

            migrationBuilder.DropColumn(
                name: "SourceDriveId",
                table: "BulkRequestAuthorizations");

            migrationBuilder.DropColumn(
                name: "SourceFolderItemId",
                table: "BulkRequestAuthorizations");

            migrationBuilder.DropColumn(
                name: "SourceTenantId",
                table: "BulkRequestAuthorizations");

            migrationBuilder.DropColumn(
                name: "ConfigurationVersion",
                table: "BulkImportSettings");

            migrationBuilder.RenameColumn(
                name: "ConnectionId",
                table: "BulkScoringRequests",
                newName: "JobConfigVersionId");

            migrationBuilder.RenameColumn(
                name: "RasterDpiUsed",
                table: "BulkScoringItems",
                newName: "PageCount");

            migrationBuilder.RenameColumn(
                name: "SourceAccountBindingHash",
                table: "BulkRequestAuthorizations",
                newName: "RecruiterActorId");

            migrationBuilder.AddColumn<string>(
                name: "AccessModel",
                table: "BulkScoringRequests",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LimitsJson",
                table: "BulkScoringRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OwningStack",
                table: "BulkScoringRequests",
                type: "TEXT",
                maxLength: 1,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_Status_ExpiresAt",
                table: "BulkScoringRequests",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_Status_OwningStack",
                table: "BulkScoringRequests",
                columns: new[] { "Status", "OwningStack" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringRequests_AccessModel",
                table: "BulkScoringRequests",
                sql: "AccessModel IN ('same_tenant', 'cross_tenant')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringRequests_OwningStack",
                table: "BulkScoringRequests",
                sql: "(AccessModel = 'same_tenant' AND OwningStack IS NOT NULL AND OwningStack IN ('A', 'B')) OR (AccessModel = 'cross_tenant' AND OwningStack IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringRequests_SourceKind",
                table: "BulkScoringRequests",
                sql: "SourceKind IN ('sharepoint', 'onedrive')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringItems_Reason",
                table: "BulkScoringItems",
                sql: "ReasonCode IS NULL OR ReasonCode IN ('unsupported_type', 'file_too_large', 'limit_exceeded', 'empty_file', 'not_convertible', 'access_denied', 'authorization_lapsed', 'source_missing', 'source_changed', 'duplicate_in_request', 'duplicate_in_job', 'conversion_failed', 'conversion_timeout', 'converted_too_large', 'scoring_failed', 'cancelled_by_user', 'transient_exhausted')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkRequestAuthorizations_Status",
                table: "BulkRequestAuthorizations",
                sql: "Status IN ('active', 'lapsed', 'discarded')");
        }
    }
}
