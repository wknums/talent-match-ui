using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncBulkV3Model : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var schema = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal)
                ? "talentmatch"
                : null;

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringItemContents_Kind",
                schema: schema,
                table: "BulkScoringItemContents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringItemContents_Kind",
                schema: schema,
                table: "BulkScoringItemContents",
                sql: "Kind IN ('original', 'converted', 'transient')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal)
                ? "talentmatch"
                : null;

            migrationBuilder.DropCheckConstraint(
                name: "CK_BulkScoringItemContents_Kind",
                schema: schema,
                table: "BulkScoringItemContents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BulkScoringItemContents_Kind",
                schema: schema,
                table: "BulkScoringItemContents",
                sql: "Kind IN ('original', 'converted')");
        }
    }
}
