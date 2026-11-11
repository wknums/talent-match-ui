using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatedByToJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "Jobs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Jobs");
        }
    }
}
