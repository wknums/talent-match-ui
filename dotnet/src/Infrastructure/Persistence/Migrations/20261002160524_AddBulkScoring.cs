using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sqlServer = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal);
            var schema = sqlServer ? "talentmatch" : null;
            var integerType = sqlServer ? "int" : "INTEGER";
            var longType = sqlServer ? "bigint" : "INTEGER";
            var booleanType = sqlServer ? "bit" : "INTEGER";
            var dateTimeType = sqlServer ? "datetime2" : "TEXT";
            var blobType = sqlServer ? "varbinary(max)" : "BLOB";
            var longTextType = sqlServer ? "nvarchar(max)" : "TEXT";
            string TextType(int length) => sqlServer ? $"nvarchar({length})" : "TEXT";
            migrationBuilder.CreateTable(
                name: "BulkImportSettings",
                schema: schema,
                columns: table => new
                {
                    Id = table.Column<int>(type: integerType, nullable: false),
                    MaxFilesPerRequest = table.Column<int>(type: integerType, nullable: false),
                    ConcurrencyVersion = table.Column<int>(type: integerType, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    UpdatedByActorId = table.Column<string>(type: TextType(128), maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkImportSettings", x => x.Id);
                    table.CheckConstraint("CK_BulkImportSettings_MaxFiles", "MaxFilesPerRequest > 0");
                    table.CheckConstraint("CK_BulkImportSettings_Singleton", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "BulkScoringRequests",
                schema: schema,
                columns: table => new
                {
                    Id = table.Column<string>(type: TextType(36), maxLength: 36, nullable: false),
                    JobId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: false),
                    OwnerActorId = table.Column<string>(type: TextType(128), maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: TextType(20), maxLength: 20, nullable: true),
                    SourceKind = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    SourceDriveId = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    SourceFolderItemId = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    SourceFolderName = table.Column<string>(type: TextType(500), maxLength: 500, nullable: false),
                    SourceFolderPath = table.Column<string>(type: TextType(2000), maxLength: 2000, nullable: false),
                    SourceWebUrl = table.Column<string>(type: TextType(2048), maxLength: 2048, nullable: false),
                    AccessModel = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    SourceTenantId = table.Column<string>(type: TextType(64), maxLength: 64, nullable: false),
                    OwningStack = table.Column<string>(type: TextType(1), maxLength: 1, nullable: true),
                    ConvertToRasterPdf = table.Column<bool>(type: booleanType, nullable: false),
                    AllowDuplicates = table.Column<bool>(type: booleanType, nullable: false),
                    JobConfigVersionId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: true),
                    LimitsJson = table.Column<string>(type: longTextType, nullable: false),
                    DiscoveryStatus = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    DiscoveryCursorJson = table.Column<string>(type: longTextType, nullable: true),
                    EntriesScanned = table.Column<int>(type: integerType, nullable: false),
                    DiscoveryStepCount = table.Column<int>(type: integerType, nullable: false),
                    ExcludedByReasonJson = table.Column<string>(type: longTextType, nullable: false),
                    RetryOfRequestId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    CancelRequestedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    CancelledByActorId = table.Column<string>(type: TextType(128), maxLength: 128, nullable: true),
                    TerminalAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    ConcurrencyVersion = table.Column<int>(type: integerType, nullable: false),
                    CorrelationId = table.Column<string>(type: TextType(64), maxLength: 64, nullable: false),
                    DiscoveredCount = table.Column<int>(type: integerType, nullable: false),
                    ExcludedCount = table.Column<int>(type: integerType, nullable: false),
                    InaccessibleCount = table.Column<int>(type: integerType, nullable: false),
                    WaitingCount = table.Column<int>(type: integerType, nullable: false),
                    ImportingCount = table.Column<int>(type: integerType, nullable: false),
                    ConvertingCount = table.Column<int>(type: integerType, nullable: false),
                    ScoringCount = table.Column<int>(type: integerType, nullable: false),
                    CompletedCount = table.Column<int>(type: integerType, nullable: false),
                    FailedCount = table.Column<int>(type: integerType, nullable: false),
                    SkippedCount = table.Column<int>(type: integerType, nullable: false),
                    CancelledCount = table.Column<int>(type: integerType, nullable: false),
                    TotalEligibleBytes = table.Column<long>(type: longType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkScoringRequests", x => x.Id);
                    table.CheckConstraint("CK_BulkScoringRequests_AccessModel", "AccessModel IN ('same_tenant', 'cross_tenant')");
                    table.CheckConstraint("CK_BulkScoringRequests_Counts", "DiscoveredCount >= 0 AND ExcludedCount >= 0 AND InaccessibleCount >= 0 AND WaitingCount >= 0 AND ImportingCount >= 0 AND ConvertingCount >= 0 AND ScoringCount >= 0 AND CompletedCount >= 0 AND FailedCount >= 0 AND SkippedCount >= 0 AND CancelledCount >= 0 AND TotalEligibleBytes >= 0 AND EntriesScanned >= 0 AND DiscoveryStepCount >= 0");
                    table.CheckConstraint("CK_BulkScoringRequests_DiscoveryStatus", "DiscoveryStatus IN ('more_available', 'complete', 'limit_reached')");
                    table.CheckConstraint("CK_BulkScoringRequests_Outcome", "Outcome IS NULL OR Outcome IN ('all_succeeded', 'partial', 'failed', 'cancelled')");
                    table.CheckConstraint("CK_BulkScoringRequests_OutcomeIffCompleted", "(Outcome IS NULL AND Status <> 'completed') OR (Outcome IS NOT NULL AND Status = 'completed')");
                    table.CheckConstraint("CK_BulkScoringRequests_OwningStack", "(AccessModel = 'same_tenant' AND OwningStack IS NOT NULL AND OwningStack IN ('A', 'B')) OR (AccessModel = 'cross_tenant' AND OwningStack IS NULL)");
                    table.CheckConstraint("CK_BulkScoringRequests_SourceKind", "SourceKind IN ('sharepoint', 'onedrive')");
                    table.CheckConstraint("CK_BulkScoringRequests_Status", "Status IN ('draft', 'active', 'completed')");
                    table.ForeignKey(
                        name: "FK_BulkScoringRequests_BulkScoringRequests_RetryOfRequestId",
                        column: x => x.RetryOfRequestId,
                        principalSchema: schema,
                        principalTable: "BulkScoringRequests",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BulkScoringRequests_Jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: schema,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BulkRequestAuthorizations",
                schema: schema,
                columns: table => new
                {
                    RequestId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: false),
                    RecruiterActorId = table.Column<string>(type: TextType(128), maxLength: 128, nullable: false),
                    Payload = table.Column<byte[]>(type: blobType, nullable: true),
                    WrappedDataKey = table.Column<byte[]>(type: blobType, nullable: true),
                    KeyVersion = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    LapseReason = table.Column<string>(type: TextType(100), maxLength: 100, nullable: true),
                    ObtainedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    LapsedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    DiscardedAt = table.Column<DateTime>(type: dateTimeType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkRequestAuthorizations", x => x.RequestId);
                    table.CheckConstraint("CK_BulkRequestAuthorizations_NoMaterialWhenInactive", "Status = 'active' OR (Payload IS NULL AND WrappedDataKey IS NULL)");
                    table.CheckConstraint("CK_BulkRequestAuthorizations_Status", "Status IN ('active', 'lapsed', 'discarded')");
                    table.ForeignKey(
                        name: "FK_BulkRequestAuthorizations_BulkScoringRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: schema,
                        principalTable: "BulkScoringRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BulkScoringItems",
                schema: schema,
                columns: table => new
                {
                    Id = table.Column<string>(type: TextType(36), maxLength: 36, nullable: false),
                    RequestId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: false),
                    Ordinal = table.Column<int>(type: integerType, nullable: false),
                    SourceDriveId = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    SourceDriveItemId = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    SourceETag = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: TextType(500), maxLength: 500, nullable: false),
                    SourcePath = table.Column<string>(type: TextType(2000), maxLength: 2000, nullable: false),
                    MimeType = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: longType, nullable: false),
                    ExcludedWhenRasterized = table.Column<bool>(type: booleanType, nullable: false),
                    Status = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    ReasonCode = table.Column<string>(type: TextType(40), maxLength: 40, nullable: true),
                    ReasonMessage = table.Column<string>(type: TextType(1000), maxLength: 1000, nullable: true),
                    Fingerprint = table.Column<string>(type: TextType(64), maxLength: 64, nullable: true),
                    FingerprintClaimed = table.Column<bool>(type: booleanType, nullable: false),
                    ApplicationId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: true),
                    DocumentId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: true),
                    ScoredVersion = table.Column<string>(type: TextType(20), maxLength: 20, nullable: true),
                    PageCount = table.Column<int>(type: integerType, nullable: true),
                    ConvertedSizeBytes = table.Column<long>(type: longType, nullable: true),
                    SourceStoredAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    ConvertedStoredAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    ApplicationCreatedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    HandedOffAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    Attempt = table.Column<int>(type: integerType, nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    LeaseOwner = table.Column<string>(type: TextType(100), maxLength: 100, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    RetryOfItemId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    TerminalAt = table.Column<DateTime>(type: dateTimeType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkScoringItems", x => x.Id);
                    table.CheckConstraint("CK_BulkScoringItems_Reason", "ReasonCode IS NULL OR ReasonCode IN ('unsupported_type', 'file_too_large', 'limit_exceeded', 'empty_file', 'not_convertible', 'access_denied', 'authorization_lapsed', 'source_missing', 'source_changed', 'duplicate_in_request', 'duplicate_in_job', 'conversion_failed', 'conversion_timeout', 'converted_too_large', 'scoring_failed', 'cancelled_by_user', 'transient_exhausted')");
                    table.CheckConstraint("CK_BulkScoringItems_ScoredVersion", "ScoredVersion IS NULL OR ScoredVersion IN ('original', 'converted')");
                    table.CheckConstraint("CK_BulkScoringItems_Sizes", "SizeBytes >= 0 AND Attempt >= 0");
                    table.CheckConstraint("CK_BulkScoringItems_Status", "Status IN ('waiting', 'importing', 'converting', 'scoring', 'completed', 'failed', 'skipped', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_BulkScoringItems_BulkScoringItems_RetryOfItemId",
                        column: x => x.RetryOfItemId,
                        principalSchema: schema,
                        principalTable: "BulkScoringItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BulkScoringItems_BulkScoringRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: schema,
                        principalTable: "BulkScoringRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BulkScoringItemContents",
                schema: schema,
                columns: table => new
                {
                    ItemId = table.Column<string>(type: TextType(36), maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    BlobUri = table.Column<string>(type: TextType(2048), maxLength: 2048, nullable: true),
                    ContentBase64 = table.Column<string>(type: longTextType, nullable: true),
                    SizeBytes = table.Column<long>(type: longType, nullable: false),
                    Sha256 = table.Column<string>(type: TextType(64), maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkScoringItemContents", x => new { x.ItemId, x.Kind });
                    table.CheckConstraint("CK_BulkScoringItemContents_Kind", "Kind IN ('original', 'converted')");
                    table.CheckConstraint("CK_BulkScoringItemContents_OneLocation", "(BlobUri IS NOT NULL AND ContentBase64 IS NULL) OR (BlobUri IS NULL AND ContentBase64 IS NOT NULL)");
                    table.CheckConstraint("CK_BulkScoringItemContents_Size", "SizeBytes >= 0");
                    table.ForeignKey(
                        name: "FK_BulkScoringItemContents_BulkScoringItems_ItemId",
                        column: x => x.ItemId,
                        principalSchema: schema,
                        principalTable: "BulkScoringItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BulkRequestAuthorizations_Status_ObtainedAt",
                schema: schema,
                table: "BulkRequestAuthorizations",
                columns: new[] { "Status", "ObtainedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringItems_RequestId_Ordinal",
                schema: schema,
                table: "BulkScoringItems",
                columns: new[] { "RequestId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringItems_RequestId_SourceDriveItemId",
                schema: schema,
                table: "BulkScoringItems",
                columns: new[] { "RequestId", "SourceDriveItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringItems_RequestId_Status",
                schema: schema,
                table: "BulkScoringItems",
                columns: new[] { "RequestId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringItems_RetryOfItemId",
                schema: schema,
                table: "BulkScoringItems",
                column: "RetryOfItemId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringItems_Scoring",
                schema: schema,
                table: "BulkScoringItems",
                column: "Status",
                filter: "[Status] = 'scoring'");

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringItems_Status_NextAttemptAt",
                schema: schema,
                table: "BulkScoringItems",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "[Status] IN ('waiting', 'importing', 'converting')");

            migrationBuilder.CreateIndex(
                name: "UX_BulkScoringItems_Request_Fingerprint",
                schema: schema,
                table: "BulkScoringItems",
                columns: new[] { "RequestId", "Fingerprint" },
                unique: true,
                filter: "[FingerprintClaimed] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_JobId_CreatedAt",
                schema: schema,
                table: "BulkScoringRequests",
                columns: new[] { "JobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_OwnerActorId_Status_AcknowledgedAt",
                schema: schema,
                table: "BulkScoringRequests",
                columns: new[] { "OwnerActorId", "Status", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_RetryOfRequestId",
                schema: schema,
                table: "BulkScoringRequests",
                column: "RetryOfRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_Status_ExpiresAt",
                schema: schema,
                table: "BulkScoringRequests",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkScoringRequests_Status_OwningStack",
                schema: schema,
                table: "BulkScoringRequests",
                columns: new[] { "Status", "OwningStack" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal)
                ? "talentmatch"
                : null;
            migrationBuilder.DropTable(
                name: "BulkImportSettings",
                schema: schema);

            migrationBuilder.DropTable(
                name: "BulkRequestAuthorizations",
                schema: schema);

            migrationBuilder.DropTable(
                name: "BulkScoringItemContents",
                schema: schema);

            migrationBuilder.DropTable(
                name: "BulkScoringItems",
                schema: schema);

            migrationBuilder.DropTable(
                name: "BulkScoringRequests",
                schema: schema);
        }
    }
}
