using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRubricSourceToJobConfigVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // All schema changes already applied by earlier migrations:
            // - AddScoringPromptAndPromptTestRun (ScoringPrompts, PromptTestRuns, TestRunId, PromptTestRunId)
            // - AddCreatedByToJob (CreatedBy)
            // - AddRubricApprovalStatus (RubricApprovalStatus, RubricSource, RawExtractionResponse)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
