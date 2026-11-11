using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionalParallelUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sqlServer = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal);
            var schema = sqlServer ? "talentmatch" : null;
            var idType = sqlServer ? "nvarchar(36)" : "TEXT";
            var integerType = sqlServer ? "int" : "INTEGER";
            var longType = sqlServer ? "bigint" : "INTEGER";
            var booleanType = sqlServer ? "bit" : "INTEGER";
            var dateTimeType = sqlServer ? "datetime2" : "TEXT";
            string TextType(int length) => sqlServer ? $"nvarchar({length})" : "TEXT";

            migrationBuilder.CreateTable(
                name: "UploadSessions",
                schema: schema,
                columns: table => new
                {
                    Id = table.Column<string>(type: idType, maxLength: 36, nullable: false),
                    JobId = table.Column<string>(type: idType, maxLength: 36, nullable: false),
                    OwnerActorId = table.Column<string>(type: TextType(128), maxLength: 128, nullable: false),
                    OwnerDisplayName = table.Column<string>(type: TextType(200), maxLength: 200, nullable: true),
                    AllowDuplicates = table.Column<bool>(type: booleanType, nullable: false),
                    Status = table.Column<string>(type: TextType(20), maxLength: 20, nullable: false),
                    FileConcurrency = table.Column<int>(type: integerType, nullable: false),
                    MaxIndividualFileBytes = table.Column<long>(type: longType, nullable: false),
                    MaxInFlightBytes = table.Column<long>(type: longType, nullable: false),
                    TotalItemCount = table.Column<int>(type: integerType, nullable: false),
                    WaitingCount = table.Column<int>(type: integerType, nullable: false),
                    ActiveCount = table.Column<int>(type: integerType, nullable: false),
                    SucceededCount = table.Column<int>(type: integerType, nullable: false),
                    SkippedCount = table.Column<int>(type: integerType, nullable: false),
                    FailedCount = table.Column<int>(type: integerType, nullable: false),
                    InterruptedCount = table.Column<int>(type: integerType, nullable: false),
                    TerminalItemCount = table.Column<int>(type: integerType, nullable: false),
                    CorrelationId = table.Column<string>(type: TextType(64), maxLength: 64, nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    StartedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    ConcurrencyVersion = table.Column<int>(type: integerType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                    table.CheckConstraint("CK_UploadSessions_Counts", "TotalItemCount > 0 AND WaitingCount >= 0 AND ActiveCount >= 0 AND SucceededCount >= 0 AND SkippedCount >= 0 AND FailedCount >= 0 AND InterruptedCount >= 0 AND TerminalItemCount >= 0");
                    table.CheckConstraint("CK_UploadSessions_Limits", "FileConcurrency > 0 AND MaxIndividualFileBytes > 0 AND MaxInFlightBytes >= MaxIndividualFileBytes");
                    table.CheckConstraint("CK_UploadSessions_Status", "Status IN ('active', 'completed')");
                    table.ForeignKey(
                        name: "FK_UploadSessions_Jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: schema,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UploadSettings",
                schema: schema,
                columns: table => new
                {
                    Id = table.Column<string>(type: TextType(64), maxLength: 64, nullable: false),
                    FileConcurrency = table.Column<int>(type: integerType, nullable: false),
                    MaxIndividualFileBytes = table.Column<long>(type: longType, nullable: false),
                    MaxInFlightBytes = table.Column<long>(type: longType, nullable: false),
                    ConcurrencyVersion = table.Column<int>(type: integerType, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    CreatedBy = table.Column<string>(type: TextType(128), maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    UpdatedBy = table.Column<string>(type: TextType(128), maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSettings", x => x.Id);
                    table.CheckConstraint("CK_UploadSettings_FileConcurrency", "FileConcurrency > 0");
                    table.CheckConstraint("CK_UploadSettings_MaxIndividualFileBytes", "MaxIndividualFileBytes > 0");
                    table.CheckConstraint("CK_UploadSettings_MaxInFlightBytes", "MaxInFlightBytes >= MaxIndividualFileBytes");
                    table.CheckConstraint("CK_UploadSettings_Singleton", "Id = 'optional-file-upload'");
                });

            migrationBuilder.CreateTable(
                name: "UploadItems",
                schema: schema,
                columns: table => new
                {
                    Id = table.Column<string>(type: idType, maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: idType, maxLength: 36, nullable: false),
                    OccurrenceKey = table.Column<string>(type: TextType(36), maxLength: 36, nullable: false),
                    Ordinal = table.Column<int>(type: integerType, nullable: false),
                    FileName = table.Column<string>(type: TextType(500), maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: TextType(200), maxLength: 200, nullable: false),
                    RawSizeBytes = table.Column<long>(type: longType, nullable: false),
                    Status = table.Column<string>(type: TextType(32), maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: integerType, nullable: false),
                    ContentFingerprint = table.Column<string>(type: TextType(64), maxLength: 64, nullable: true),
                    ApplicationId = table.Column<string>(type: idType, maxLength: 36, nullable: true),
                    OutcomeCode = table.Column<string>(type: TextType(100), maxLength: 100, nullable: true),
                    OutcomeMessage = table.Column<string>(type: TextType(1000), maxLength: 1000, nullable: true),
                    LastHttpStatus = table.Column<int>(type: integerType, nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    NextRetryAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    ConcurrencyVersion = table.Column<int>(type: integerType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadItems", x => x.Id);
                    table.CheckConstraint("CK_UploadItems_Application", "(Status = 'succeeded' AND ApplicationId IS NOT NULL) OR (Status <> 'succeeded' AND ApplicationId IS NULL)");
                    table.CheckConstraint("CK_UploadItems_Attempts", "AttemptCount >= 0 AND AttemptCount <= 4");
                    table.CheckConstraint("CK_UploadItems_RawSizeBytes", "RawSizeBytes >= 0");
                    table.CheckConstraint("CK_UploadItems_Status", "Status IN ('waiting', 'throttled', 'uploading', 'retrying', 'succeeded', 'skipped_duplicate', 'failed', 'interrupted')");
                    table.ForeignKey(
                        name: "FK_UploadItems_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: schema,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UploadItems_UploadSessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: schema,
                        principalTable: "UploadSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UploadItems_ApplicationId",
                schema: schema,
                table: "UploadItems",
                column: "ApplicationId",
                unique: true,
                filter: "[ApplicationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UploadItems_SessionId_ContentFingerprint",
                schema: schema,
                table: "UploadItems",
                columns: new[] { "SessionId", "ContentFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadItems_SessionId_OccurrenceKey",
                schema: schema,
                table: "UploadItems",
                columns: new[] { "SessionId", "OccurrenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadItems_SessionId_Ordinal",
                schema: schema,
                table: "UploadItems",
                columns: new[] { "SessionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadItems_SessionId_Status_Ordinal",
                schema: schema,
                table: "UploadItems",
                columns: new[] { "SessionId", "Status", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadItems_Status_UpdatedAt",
                schema: schema,
                table: "UploadItems",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_JobId_CreatedAt",
                schema: schema,
                table: "UploadSessions",
                columns: new[] { "JobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_OwnerActorId_CreatedAt",
                schema: schema,
                table: "UploadSessions",
                columns: new[] { "OwnerActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_Status_LastHeartbeatAt",
                schema: schema,
                table: "UploadSessions",
                columns: new[] { "Status", "LastHeartbeatAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal)
                ? "talentmatch"
                : null;
            migrationBuilder.DropTable(
                name: "UploadItems",
                schema: schema);

            migrationBuilder.DropTable(
                name: "UploadSettings",
                schema: schema);

            migrationBuilder.DropTable(
                name: "UploadSessions",
                schema: schema);
        }
    }
}
