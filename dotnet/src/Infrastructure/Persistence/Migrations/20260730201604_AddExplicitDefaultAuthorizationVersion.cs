using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExplicitDefaultAuthorizationVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_TenantId_UserObjectId_Source_Role_OrganizationId_DepartmentId",
                table: "RoleAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrganizationMemberships_Status",
                table: "OrganizationMemberships");

            migrationBuilder.AddColumn<int>(
                name: "AuthorizationVersion",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DefaultDepartmentMembershipId",
                table: "OrganizationMemberships",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_DepartmentMemberships_Id_UserId_OrganizationId",
                table: "DepartmentMemberships",
                columns: new[] { "Id", "UserId", "OrganizationId" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE TEMP TABLE ExplicitDefaultMigrationGuard (
                        InvalidMembershipCount INTEGER NOT NULL CHECK (InvalidMembershipCount = 0)
                );
                INSERT INTO ExplicitDefaultMigrationGuard (InvalidMembershipCount)
                SELECT COUNT(*)
                FROM (
                        SELECT om.Id
                        FROM OrganizationMemberships om
                        LEFT JOIN DepartmentMemberships dm
                            ON dm.UserId = om.UserId
                         AND dm.OrganizationId = om.OrganizationId
                         AND dm.Status = 'active'
                        LEFT JOIN Departments d
                            ON d.Id = dm.DepartmentId
                         AND d.OrganizationId = dm.OrganizationId
                         AND d.Status = 'active'
                        WHERE om.Status = 'active'
                        GROUP BY om.Id
                        HAVING COUNT(*) <> 1 OR MAX(d.Id) IS NULL
                );
                DROP TABLE ExplicitDefaultMigrationGuard;

                UPDATE OrganizationMemberships
                SET DefaultDepartmentMembershipId = (
                        SELECT MIN(dm.Id)
                        FROM DepartmentMemberships dm
                        INNER JOIN Departments d
                            ON d.Id = dm.DepartmentId
                         AND d.OrganizationId = dm.OrganizationId
                        WHERE dm.UserId = OrganizationMemberships.UserId
                            AND dm.OrganizationId = OrganizationMemberships.OrganizationId
                            AND dm.Status = 'active'
                            AND d.Status = 'active'
                )
                WHERE Status = 'active';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_TenantId_UserObjectId_Role_OrganizationId_DepartmentId",
                table: "RoleAssignments",
                columns: new[] { "TenantId", "UserObjectId", "Role", "OrganizationId", "DepartmentId" },
                unique: true,
                filter: "[Status] = 'active' AND [Source] = 'delegated'");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true,
                filter: "[AuthenticationProvider] = 'simple'");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_DefaultDepartmentMembershipId_UserId_OrganizationId",
                table: "OrganizationMemberships",
                columns: new[] { "DefaultDepartmentMembershipId", "UserId", "OrganizationId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrganizationMemberships_Status",
                table: "OrganizationMemberships",
                sql: "(Status = 'active' AND RevokedAt IS NULL AND DefaultDepartmentMembershipId IS NOT NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationMemberships_DepartmentMemberships_DefaultDepartmentMembershipId_UserId_OrganizationId",
                table: "OrganizationMemberships",
                columns: new[] { "DefaultDepartmentMembershipId", "UserId", "OrganizationId" },
                principalTable: "DepartmentMemberships",
                principalColumns: new[] { "Id", "UserId", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationMemberships_DepartmentMemberships_DefaultDepartmentMembershipId_UserId_OrganizationId",
                table: "OrganizationMemberships");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_TenantId_UserObjectId_Role_OrganizationId_DepartmentId",
                table: "RoleAssignments");

            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_OrganizationMemberships_DefaultDepartmentMembershipId_UserId_OrganizationId",
                table: "OrganizationMemberships");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrganizationMemberships_Status",
                table: "OrganizationMemberships");

            migrationBuilder.DropIndex(
                name: "UX_DepartmentMemberships_Id_UserId_OrganizationId",
                table: "DepartmentMemberships");

            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DefaultDepartmentMembershipId",
                table: "OrganizationMemberships");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_TenantId_UserObjectId_Source_Role_OrganizationId_DepartmentId",
                table: "RoleAssignments",
                columns: new[] { "TenantId", "UserObjectId", "Source", "Role", "OrganizationId", "DepartmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrganizationMemberships_Status",
                table: "OrganizationMemberships",
                sql: "(Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)");
        }
    }
}
