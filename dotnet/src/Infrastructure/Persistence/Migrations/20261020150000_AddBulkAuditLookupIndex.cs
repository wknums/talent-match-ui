using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261020150000_AddBulkAuditLookupIndex")]
public sealed class AddBulkAuditLookupIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("SqlServer", StringComparison.Ordinal))
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'talentmatch.BulkAuditEvents')
                      AND name = N'IX_BulkAuditEvents_SubjectId_EventType'
                )
                    CREATE INDEX [IX_BulkAuditEvents_SubjectId_EventType]
                        ON [talentmatch].[BulkAuditEvents] ([SubjectId], [EventType]);
                """);
            return;
        }

        migrationBuilder.CreateIndex(
            name: "IX_BulkAuditEvents_SubjectId_EventType",
            table: "BulkAuditEvents",
            columns: ["SubjectId", "EventType"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(
            name: "IX_BulkAuditEvents_SubjectId_EventType",
            schema: ActiveProvider.Contains("SqlServer", StringComparison.Ordinal) ? "talentmatch" : null,
            table: "BulkAuditEvents");
}
