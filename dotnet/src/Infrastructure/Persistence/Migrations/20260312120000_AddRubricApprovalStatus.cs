using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRubricApprovalStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RubricApprovalStatus",
                table: "JobConfigVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "approved");

            migrationBuilder.AddColumn<string>(
                name: "RubricSource",
                table: "JobConfigVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "RawExtractionResponse",
                table: "JobConfigVersions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RubricApprovalStatus",
                table: "JobConfigVersions");

            migrationBuilder.DropColumn(
                name: "RubricSource",
                table: "JobConfigVersions");

            migrationBuilder.DropColumn(
                name: "RawExtractionResponse",
                table: "JobConfigVersions");
        }
    }
}
