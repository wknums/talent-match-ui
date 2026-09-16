using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractionInstructionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractionId",
                table: "JobConfigVersions",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractionInstructionVersionId",
                table: "JobConfigVersions",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExtractionInstructionVersions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    InstructionText = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedContractVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ChangeNote = table.Column<string>(type: "TEXT", nullable: true),
                    ValidationStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ValidationFindingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ValidatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ValidatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActivatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ConcurrencyVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionInstructionVersions", x => x.Id);
                    table.CheckConstraint("CK_ExtractionInstructionVersions_Status", "Status IN ('draft', 'active', 'retired')");
                    table.CheckConstraint("CK_ExtractionInstructionVersions_ValidationStatus", "ValidationStatus IN ('unvalidated', 'valid', 'invalid')");
                });

            migrationBuilder.CreateTable(
                name: "JobSpecExtractions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    InstructionVersionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProtectedContractVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SourceFileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SourceMimeType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RawResponse = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    ValidationStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ValidationFindingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: true),
                    JobConfigVersionId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSpecExtractions", x => x.Id);
                    table.CheckConstraint("CK_JobSpecExtractions_Purpose", "Purpose IN ('job_creation', 'instruction_validation')");
                    table.CheckConstraint("CK_JobSpecExtractions_ValidationStatus", "ValidationStatus IN ('valid', 'invalid')");
                    table.ForeignKey(
                        name: "FK_JobSpecExtractions_ExtractionInstructionVersions_InstructionVersionId",
                        column: x => x.InstructionVersionId,
                        principalTable: "ExtractionInstructionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobSpecExtractions_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionInstructionVersions_Status",
                table: "ExtractionInstructionVersions",
                column: "Status",
                unique: true,
                filter: "[Status] = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionInstructionVersions_Status_VersionNumber",
                table: "ExtractionInstructionVersions",
                columns: new[] { "Status", "VersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionInstructionVersions_VersionNumber",
                table: "ExtractionInstructionVersions",
                column: "VersionNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobSpecExtractions_InstructionVersionId_CreatedAt",
                table: "JobSpecExtractions",
                columns: new[] { "InstructionVersionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSpecExtractions_JobConfigVersionId",
                table: "JobSpecExtractions",
                column: "JobConfigVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSpecExtractions_JobId_CreatedAt",
                table: "JobSpecExtractions",
                columns: new[] { "JobId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobSpecExtractions");

            migrationBuilder.DropTable(
                name: "ExtractionInstructionVersions");

            migrationBuilder.DropColumn(
                name: "ExtractionId",
                table: "JobConfigVersions");

            migrationBuilder.DropColumn(
                name: "ExtractionInstructionVersionId",
                table: "JobConfigVersions");
        }
    }
}
