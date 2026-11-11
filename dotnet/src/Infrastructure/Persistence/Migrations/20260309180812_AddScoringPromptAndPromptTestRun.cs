using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScoringPromptAndPromptTestRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PromptTestRunId",
                table: "Applications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TestRunId",
                table: "Applications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScoringPrompts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptText = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Comments = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    GenerationMetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoringPrompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScoringPrompts_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PromptTestRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    PromptId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ApplicationIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewNotes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptTestRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PromptTestRuns_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PromptTestRuns_ScoringPrompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "ScoringPrompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_PromptTestRunId",
                table: "Applications",
                column: "PromptTestRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_TestRunId",
                table: "Applications",
                column: "TestRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PromptTestRuns_JobId",
                table: "PromptTestRuns",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_PromptTestRuns_PromptId",
                table: "PromptTestRuns",
                column: "PromptId");

            migrationBuilder.CreateIndex(
                name: "IX_ScoringPrompts_JobId_Status",
                table: "ScoringPrompts",
                columns: new[] { "JobId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Applications_PromptTestRuns_PromptTestRunId",
                table: "Applications",
                column: "PromptTestRunId",
                principalTable: "PromptTestRuns",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Applications_PromptTestRuns_PromptTestRunId",
                table: "Applications");

            migrationBuilder.DropTable(
                name: "PromptTestRuns");

            migrationBuilder.DropTable(
                name: "ScoringPrompts");

            migrationBuilder.DropIndex(
                name: "IX_Applications_PromptTestRunId",
                table: "Applications");

            migrationBuilder.DropIndex(
                name: "IX_Applications_TestRunId",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "PromptTestRunId",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "TestRunId",
                table: "Applications");
        }
    }
}
