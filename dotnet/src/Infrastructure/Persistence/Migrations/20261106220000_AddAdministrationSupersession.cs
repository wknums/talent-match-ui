using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261106220000_AddAdministrationSupersession")]
public sealed class AddAdministrationSupersession : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlServer = ActiveProvider.Contains("SqlServer");
        var schema = sqlServer ? "talentmatch" : null;
        migrationBuilder.AddColumn<string>("SupersedesTransitionId", "AdministrationTransitions", schema: schema,
            type: sqlServer ? "nvarchar(36)" : "TEXT", maxLength: 36, nullable: true);
        migrationBuilder.AddColumn<string>("SupersededByTransitionId", "AdministrationTransitions", schema: schema,
            type: sqlServer ? "nvarchar(36)" : "TEXT", maxLength: 36, nullable: true);
        migrationBuilder.AddColumn<DateTime>("SupersededAt", "AdministrationTransitions", schema: schema,
            type: sqlServer ? "datetime2" : "TEXT", nullable: true);
        migrationBuilder.DropIndex("IX_AdministrationTransitions_TargetKey", "AdministrationTransitions", schema);
        migrationBuilder.CreateIndex("IX_AdministrationTransitions_TargetKey", "AdministrationTransitions", "TargetKey",
            schema, unique: true, filter: sqlServer
                ? "[Phase] IN ('preflight','prepared','sql_switched','replacement_verified','authority_committed','retiring','failed','rollback_pending') AND [SupersededByTransitionId] IS NULL"
                : "[Phase] NOT IN ('complete','rolled_back') AND [SupersededByTransitionId] IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Supersession history is permanent. Business rollback is a new validated transition, not a schema downgrade.");
}
