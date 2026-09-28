using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations;

public partial class AddEntraOrganizationAuthorization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "PasswordHash",
            table: "Users",
            type: "TEXT",
            maxLength: 128,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT");

        migrationBuilder.AddColumn<string>(name: "AuthenticationProvider", table: "Users", type: "TEXT", maxLength: 20, nullable: false, defaultValue: "simple");
        migrationBuilder.AddColumn<string>(name: "EntraObjectId", table: "Users", type: "TEXT", maxLength: 36, nullable: true);
        migrationBuilder.AddColumn<string>(name: "EntraTenantId", table: "Users", type: "TEXT", maxLength: 36, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsActive", table: "Users", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<bool>(name: "PasswordResetRequired", table: "Users", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(name: "DepartmentId", table: "Jobs", type: "TEXT", maxLength: 36, nullable: true);
        migrationBuilder.AddColumn<string>(name: "OrganizationId", table: "Jobs", type: "TEXT", maxLength: 36, nullable: true);

        migrationBuilder.CreateTable(
            name: "Organizations",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "active"),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Organizations", x => x.Id);
                table.CheckConstraint("CK_Organizations_Status", "Status IN ('active', 'retired')");
            });

        migrationBuilder.CreateTable(
            name: "RoleGroupMappings",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                GroupObjectId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                Role = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                OrganizationId = table.Column<string>(type: "TEXT", nullable: true),
                DepartmentId = table.Column<string>(type: "TEXT", nullable: true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoleGroupMappings", x => x.Id);
                table.CheckConstraint("CK_RoleGroupMappings_Scope", "(Role = 'admin' AND OrganizationId IS NULL AND DepartmentId IS NULL) OR (Role = 'organization_admin' AND OrganizationId IS NOT NULL AND DepartmentId IS NULL) OR (Role = 'recruiter' AND OrganizationId IS NOT NULL AND DepartmentId IS NOT NULL) OR (Role = 'business_panel' AND OrganizationId IS NOT NULL)");
            });

        migrationBuilder.CreateTable(
            name: "Departments",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                OrganizationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "active"),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Departments", x => x.Id);
                table.UniqueConstraint("AK_Departments_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                table.CheckConstraint("CK_Departments_Status", "Status IN ('active', 'retired')");
                table.ForeignKey("FK_Departments_Organizations_OrganizationId", x => x.OrganizationId, "Organizations", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "OrganizationMemberships",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                OrganizationId = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "active"),
                EffectiveAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrganizationMemberships", x => x.Id);
                table.CheckConstraint("CK_OrganizationMemberships_Status", "(Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)");
                table.ForeignKey("FK_OrganizationMemberships_Organizations_OrganizationId", x => x.OrganizationId, "Organizations", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_OrganizationMemberships_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RoleAssignments",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                UserObjectId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                Role = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                OrganizationId = table.Column<string>(type: "TEXT", nullable: true),
                DepartmentId = table.Column<string>(type: "TEXT", nullable: true),
                RoleGroupMappingId = table.Column<string>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "active"),
                EffectiveAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                table.CheckConstraint("CK_RoleAssignments_Status", "(Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)");
                table.ForeignKey("FK_RoleAssignments_RoleGroupMappings_RoleGroupMappingId", x => x.RoleGroupMappingId, "RoleGroupMappings", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_RoleAssignments_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DepartmentMemberships",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                OrganizationId = table.Column<string>(type: "TEXT", nullable: false),
                DepartmentId = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "active"),
                EffectiveAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DepartmentMemberships", x => x.Id);
                table.CheckConstraint("CK_DepartmentMemberships_Status", "(Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)");
                table.ForeignKey("FK_DepartmentMemberships_Departments_DepartmentId_OrganizationId", x => new { x.DepartmentId, x.OrganizationId }, "Departments", new[] { "Id", "OrganizationId" }, onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_DepartmentMemberships_Organizations_OrganizationId", x => x.OrganizationId, "Organizations", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_DepartmentMemberships_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Users_EntraTenantId_EntraObjectId", "Users", new[] { "EntraTenantId", "EntraObjectId" }, unique: true, filter: "[AuthenticationProvider] = 'entra'");
        migrationBuilder.AddCheckConstraint("CK_Users_IdentityProvider", "Users", "(AuthenticationProvider = 'simple' AND PasswordHash IS NOT NULL AND EntraTenantId IS NULL AND EntraObjectId IS NULL) OR (AuthenticationProvider = 'entra' AND PasswordHash IS NULL AND EntraTenantId IS NOT NULL AND EntraObjectId IS NOT NULL AND PasswordResetRequired = 0)");
        migrationBuilder.CreateIndex("IX_Organizations_Name", "Organizations", "Name", unique: true, filter: "[Status] = 'active'");
        migrationBuilder.CreateIndex("IX_Departments_OrganizationId_Name", "Departments", new[] { "OrganizationId", "Name" }, unique: true, filter: "[Status] = 'active'");
        migrationBuilder.CreateIndex("IX_OrganizationMemberships_UserId_OrganizationId", "OrganizationMemberships", new[] { "UserId", "OrganizationId" }, unique: true, filter: "[Status] = 'active'");
        migrationBuilder.CreateIndex("IX_OrganizationMemberships_OrganizationId", "OrganizationMemberships", "OrganizationId");
        migrationBuilder.CreateIndex("IX_DepartmentMemberships_UserId_DepartmentId", "DepartmentMemberships", new[] { "UserId", "DepartmentId" }, unique: true, filter: "[Status] = 'active'");
        migrationBuilder.CreateIndex("IX_DepartmentMemberships_DepartmentId_OrganizationId", "DepartmentMemberships", new[] { "DepartmentId", "OrganizationId" });
        migrationBuilder.CreateIndex("IX_DepartmentMemberships_OrganizationId", "DepartmentMemberships", "OrganizationId");
        migrationBuilder.CreateIndex("IX_RoleGroupMappings_TenantId_GroupObjectId", "RoleGroupMappings", new[] { "TenantId", "GroupObjectId" }, unique: true);
        migrationBuilder.CreateIndex("IX_RoleAssignments_TenantId_UserObjectId_RoleGroupMappingId", "RoleAssignments", new[] { "TenantId", "UserObjectId", "RoleGroupMappingId" }, unique: true, filter: "[Status] = 'active' AND [RoleGroupMappingId] IS NOT NULL");
        migrationBuilder.CreateIndex("IX_RoleAssignments_TenantId_UserObjectId_Source_Role_OrganizationId_DepartmentId", "RoleAssignments", new[] { "TenantId", "UserObjectId", "Source", "Role", "OrganizationId", "DepartmentId" }, unique: true);
        migrationBuilder.CreateIndex("IX_RoleAssignments_RoleGroupMappingId", "RoleAssignments", "RoleGroupMappingId");
        migrationBuilder.CreateIndex("IX_RoleAssignments_UserId", "RoleAssignments", "UserId");
        migrationBuilder.CreateIndex("IX_Jobs_DepartmentId_OrganizationId", "Jobs", new[] { "DepartmentId", "OrganizationId" });
        migrationBuilder.CreateIndex("IX_Jobs_OrganizationId_DepartmentId", "Jobs", new[] { "OrganizationId", "DepartmentId" });
        migrationBuilder.AddForeignKey(
            name: "FK_Jobs_Departments_DepartmentId_OrganizationId",
            table: "Jobs",
            columns: new[] { "DepartmentId", "OrganizationId" },
            principalTable: "Departments",
            principalColumns: new[] { "Id", "OrganizationId" },
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_Jobs_Organizations_OrganizationId", "Jobs", "OrganizationId", "Organizations", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Jobs_Departments_DepartmentId_OrganizationId", "Jobs");
        migrationBuilder.DropForeignKey("FK_Jobs_Organizations_OrganizationId", "Jobs");
        migrationBuilder.DropTable("DepartmentMemberships");
        migrationBuilder.DropTable("OrganizationMemberships");
        migrationBuilder.DropTable("RoleAssignments");
        migrationBuilder.DropTable("Departments");
        migrationBuilder.DropTable("RoleGroupMappings");
        migrationBuilder.DropTable("Organizations");
        migrationBuilder.DropIndex("IX_Users_EntraTenantId_EntraObjectId", "Users");
        migrationBuilder.DropCheckConstraint("CK_Users_IdentityProvider", "Users");
        migrationBuilder.DropColumn("AuthenticationProvider", "Users");
        migrationBuilder.DropColumn("EntraObjectId", "Users");
        migrationBuilder.DropColumn("EntraTenantId", "Users");
        migrationBuilder.DropColumn("IsActive", "Users");
        migrationBuilder.DropColumn("PasswordResetRequired", "Users");
        migrationBuilder.DropColumn("DepartmentId", "Jobs");
        migrationBuilder.DropColumn("OrganizationId", "Jobs");
        migrationBuilder.AlterColumn<string>(name: "PasswordHash", table: "Users", type: "TEXT", nullable: false, defaultValue: "", oldClrType: typeof(string), oldType: "TEXT", oldMaxLength: 128, oldNullable: true);
    }
}
