using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261014140208_AddBulkAuditLedger")]
public sealed class AddBulkAuditLedger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var schema = ActiveProvider.Contains("SqlServer", StringComparison.Ordinal)
            ? "talentmatch"
            : null;

        migrationBuilder.CreateTable(
            name: "BulkAuditEvents",
            schema: schema,
            columns: table => new
            {
                AuditEventId = table.Column<string>(maxLength: 36, nullable: false),
                EventType = table.Column<string>(maxLength: 100, nullable: false),
                CorrelationId = table.Column<string>(maxLength: 36, nullable: false),
                ActorType = table.Column<string>(maxLength: 20, nullable: false),
                ActorId = table.Column<string>(maxLength: 128, nullable: false),
                InitiatedByActorId = table.Column<string>(maxLength: 128, nullable: true),
                OccurredAt = table.Column<DateTime>(nullable: false),
                SubjectType = table.Column<string>(maxLength: 100, nullable: false),
                SubjectId = table.Column<string>(maxLength: 128, nullable: false),
                SafeFactsJson = table.Column<string>(nullable: false),
                SchemaVersion = table.Column<int>(nullable: false),
                Boundary = table.Column<string>(maxLength: 20, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BulkAuditEvents", x => x.AuditEventId);
                table.CheckConstraint(
                    "CK_BulkAuditEvents_ActorType",
                    "ActorType IN ('user', 'service')");
                table.CheckConstraint(
                    "CK_BulkAuditEvents_Boundary",
                    "Boundary IN ('transactional', 'durable_outbox', 'reconciliation')");
                table.CheckConstraint("CK_BulkAuditEvents_SchemaVersion", "SchemaVersion > 0");
            });

        migrationBuilder.CreateIndex(
            name: "IX_BulkAuditEvents_CorrelationId",
            schema: schema,
            table: "BulkAuditEvents",
            column: "CorrelationId");
        migrationBuilder.CreateIndex(
            name: "IX_BulkAuditEvents_SubjectType_SubjectId_OccurredAt",
            schema: schema,
            table: "BulkAuditEvents",
            columns: ["SubjectType", "SubjectId", "OccurredAt"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BulkAuditEvents",
            schema: ActiveProvider.Contains("SqlServer", StringComparison.Ordinal)
                ? "talentmatch"
                : null);
    }
}
