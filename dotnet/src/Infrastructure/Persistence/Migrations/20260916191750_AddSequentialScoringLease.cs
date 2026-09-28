using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSequentialScoringLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sqlServer = ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer";
            var schema = sqlServer ? "talentmatch" : null;
            migrationBuilder.AddColumn<DateTime>(
                name: "ScoringLeaseUntil",
                schema: schema,
                table: "Applications",
                type: sqlServer ? "datetime2" : "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScoringOwner",
                schema: schema,
                table: "Applications",
                type: sqlServer ? "nvarchar(128)" : "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_Status_ScoringLeaseUntil",
                schema: schema,
                table: "Applications",
                columns: new[] { "Status", "ScoringLeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_Status_TestRunId_CreatedAt",
                schema: schema,
                table: "Applications",
                columns: new[] { "Status", "TestRunId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer" ? "talentmatch" : null;
            migrationBuilder.DropIndex(
                name: "IX_Applications_Status_ScoringLeaseUntil",
                schema: schema,
                table: "Applications");

            migrationBuilder.DropIndex(
                name: "IX_Applications_Status_TestRunId_CreatedAt",
                schema: schema,
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "ScoringLeaseUntil",
                schema: schema,
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "ScoringOwner",
                schema: schema,
                table: "Applications");
        }
    }
}
