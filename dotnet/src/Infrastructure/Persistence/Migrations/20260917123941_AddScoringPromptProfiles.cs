using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScoringPromptProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReasoningLevel",
                table: "ScoringRuns",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ApprovedModelId",
                table: "ScoringPrompts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedReasoningLevel",
                table: "ScoringPrompts",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedTestRunId",
                table: "ScoringPrompts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationInstructionVersionId",
                table: "ScoringPrompts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "ScoringPrompts",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReasoningLevel",
                table: "ScoringPrompts",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ApprovedModelId",
                table: "PromptTestRuns",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedReasoningLevel",
                table: "PromptTestRuns",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "PromptTestRuns",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReasoningLevel",
                table: "PromptTestRuns",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PromptGenerationInstructions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: true),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    InstructionText = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ChangeNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActivatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptGenerationInstructions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromptGenerationInstructions_JobId_Status",
                table: "PromptGenerationInstructions",
                columns: new[] { "JobId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptGenerationInstructions_JobId_VersionNumber",
                table: "PromptGenerationInstructions",
                columns: new[] { "JobId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromptGenerationInstructions");

            migrationBuilder.DropColumn(
                name: "ReasoningLevel",
                table: "ScoringRuns");

            migrationBuilder.DropColumn(
                name: "ApprovedModelId",
                table: "ScoringPrompts");

            migrationBuilder.DropColumn(
                name: "ApprovedReasoningLevel",
                table: "ScoringPrompts");

            migrationBuilder.DropColumn(
                name: "ApprovedTestRunId",
                table: "ScoringPrompts");

            migrationBuilder.DropColumn(
                name: "GenerationInstructionVersionId",
                table: "ScoringPrompts");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "ScoringPrompts");

            migrationBuilder.DropColumn(
                name: "ReasoningLevel",
                table: "ScoringPrompts");

            migrationBuilder.DropColumn(
                name: "ApprovedModelId",
                table: "PromptTestRuns");

            migrationBuilder.DropColumn(
                name: "ApprovedReasoningLevel",
                table: "PromptTestRuns");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "PromptTestRuns");

            migrationBuilder.DropColumn(
                name: "ReasoningLevel",
                table: "PromptTestRuns");
        }
    }
}
