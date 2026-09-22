using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptReasoningSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "PromptGenerationInstructions",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReasoningLevel",
                table: "PromptGenerationInstructions",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "ExtractionInstructionVersions",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReasoningLevel",
                table: "ExtractionInstructionVersions",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "PromptGenerationInstructions");

            migrationBuilder.DropColumn(
                name: "ReasoningLevel",
                table: "PromptGenerationInstructions");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "ExtractionInstructionVersions");

            migrationBuilder.DropColumn(
                name: "ReasoningLevel",
                table: "ExtractionInstructionVersions");
        }
    }
}
