using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUiPollingIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sqlServer = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal);
            var schema = sqlServer ? "talentmatch" : null;
            var type = sqlServer ? "int" : "INTEGER";
            migrationBuilder.AddColumn<int>(
                name: "StatusPollingIntervalSeconds",
                schema: schema,
                table: "UploadSettings",
                type: type,
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "ThroughputRefreshIntervalSeconds",
                schema: schema,
                table: "UploadSettings",
                type: type,
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddCheckConstraint(
                name: "CK_UploadSettings_StatusPolling",
                schema: schema,
                table: "UploadSettings",
                sql: "StatusPollingIntervalSeconds BETWEEN 1 AND 4294967");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UploadSettings_ThroughputRefresh",
                schema: schema,
                table: "UploadSettings",
                sql: "ThroughputRefreshIntervalSeconds BETWEEN 1 AND 4294967");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var schema = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal) ? "talentmatch" : null;
            migrationBuilder.DropCheckConstraint(
                name: "CK_UploadSettings_StatusPolling",
                schema: schema,
                table: "UploadSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_UploadSettings_ThroughputRefresh",
                schema: schema,
                table: "UploadSettings");

            migrationBuilder.DropColumn(
                name: "StatusPollingIntervalSeconds",
                schema: schema,
                table: "UploadSettings");

            migrationBuilder.DropColumn(
                name: "ThroughputRefreshIntervalSeconds",
                schema: schema,
                table: "UploadSettings");
        }
    }
}
