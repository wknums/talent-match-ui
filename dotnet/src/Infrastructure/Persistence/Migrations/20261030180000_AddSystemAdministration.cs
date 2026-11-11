using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalentMatch.Infrastructure.Persistence.Migrations;

public partial class AddSystemAdministration : Migration
{
    private bool SqlServer => ActiveProvider.Contains("SqlServer");
    private string Schema => SqlServer ? "talentmatch" : null;
    private string Text(int? length = null) => SqlServer ? $"nvarchar({(length.HasValue ? length.Value.ToString() : "max")})" : "TEXT";
    private string Integer => SqlServer ? "int" : "INTEGER";
    private string Timestamp => SqlServer ? "datetime2" : "TEXT";
    private string Boolean => SqlServer ? "bit" : "INTEGER";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SqlServer ? """
            IF EXISTS (SELECT 1 FROM [talentmatch].[RoleAssignments]
                WHERE Source NOT IN ('group','delegated','bootstrap')
                OR (Source = 'group' AND RoleGroupMappingId IS NULL)
                OR (Source IN ('delegated','bootstrap') AND RoleGroupMappingId IS NOT NULL))
                THROW 51000, 'administration_upgrade_requires_valid_assignment_sources', 1;
            IF EXISTS (SELECT 1 FROM [talentmatch].[RoleAssignments]
                WHERE Status = 'active' AND Source = 'delegated' AND DepartmentId IS NULL
                GROUP BY TenantId, UserObjectId, Role, OrganizationId HAVING COUNT(*) > 1)
                THROW 51000, 'administration_upgrade_requires_unique_delegated_scopes', 1;
            """ : """
            CREATE TABLE __AdministrationUpgradeGuard (InvalidCount INTEGER NOT NULL CHECK (InvalidCount = 0));
            INSERT INTO __AdministrationUpgradeGuard
            SELECT COUNT(*) FROM RoleAssignments
            WHERE Source NOT IN ('group','delegated','bootstrap')
              OR (Source = 'group' AND RoleGroupMappingId IS NULL)
              OR (Source IN ('delegated','bootstrap') AND RoleGroupMappingId IS NOT NULL);
            INSERT INTO __AdministrationUpgradeGuard
            SELECT COUNT(*) FROM (
                SELECT TenantId, UserObjectId, Role, OrganizationId
                FROM RoleAssignments WHERE Status = 'active' AND Source = 'delegated' AND DepartmentId IS NULL
                GROUP BY TenantId, UserObjectId, Role, OrganizationId HAVING COUNT(*) > 1
            );
            DROP TABLE __AdministrationUpgradeGuard;
            """);

        // Historical EF migrations use the old audit names; new atomic administration audits
        // use the shared schema's Action/DetailsJson names without rewriting audit records.
        if (SqlServer)
            migrationBuilder.Sql("""
                IF COL_LENGTH('talentmatch.ProcessingEvents', 'Action') IS NULL
                    EXEC sp_rename 'talentmatch.ProcessingEvents.EventType', 'Action', 'COLUMN';
                IF COL_LENGTH('talentmatch.ProcessingEvents', 'DetailsJson') IS NULL
                    EXEC sp_rename 'talentmatch.ProcessingEvents.PayloadJson', 'DetailsJson', 'COLUMN';
                """);
        else
        {
            migrationBuilder.RenameColumn("EventType", "ProcessingEvents", "Action");
            migrationBuilder.RenameColumn("PayloadJson", "ProcessingEvents", "DetailsJson");
        }

        migrationBuilder.DropIndex("IX_RoleAssignments_TenantId_UserObjectId_RoleGroupMappingId", "RoleAssignments", Schema);
        migrationBuilder.AddColumn<string>("Purpose", "RoleGroupMappings", type: Text(20), schema: Schema, maxLength: 20, nullable: false, defaultValue: "scoped");
        migrationBuilder.AddColumn<string>("ManagementSource", "RoleAssignments", type: Text(20), schema: Schema, maxLength: 20, nullable: false, defaultValue: "independent");
        migrationBuilder.AddColumn<string>("ManagedTransitionId", "RoleAssignments", type: Text(36), schema: Schema, maxLength: 36, nullable: true);
        migrationBuilder.AddUniqueConstraint("AK_RoleGroupMappings_Id_TenantId_GroupObjectId", "RoleGroupMappings",
            new[] { "Id", "TenantId", "GroupObjectId" }, Schema);
        migrationBuilder.AddCheckConstraint("CK_RoleGroupMappings_Purpose", "RoleGroupMappings", "Purpose IN ('scoped','system_admin')", Schema);
        migrationBuilder.AddCheckConstraint("CK_RoleAssignments_Source", "RoleAssignments",
            "(Source = 'group' AND RoleGroupMappingId IS NOT NULL) OR (Source IN ('delegated','bootstrap') AND RoleGroupMappingId IS NULL)", Schema);
        migrationBuilder.AddCheckConstraint("CK_RoleAssignments_Management", "RoleAssignments", "ManagementSource IN ('deployment','application','independent')", Schema);

        migrationBuilder.CreateTable("AdministrationTransitions", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            TargetKey = table.Column<string>(type: Text(300), maxLength: 300, nullable: false),
            DesiredFingerprint = table.Column<string>(type: Text(64), maxLength: 64, nullable: false),
            PriorConfiguration = table.Column<string>(type: Text(), nullable: true),
            CandidateConfiguration = table.Column<string>(type: Text(), nullable: false),
            Phase = table.Column<string>(type: Text(32), maxLength: 32, nullable: false),
            CommittedVersion = table.Column<int>(type: Integer, nullable: false),
            CandidateVersion = table.Column<int>(type: Integer, nullable: false),
            LeaseOwner = table.Column<string>(type: Text(128), maxLength: 128, nullable: false),
            LeaseExpiresAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            FencingToken = table.Column<long>(type: SqlServer ? "bigint" : "INTEGER", nullable: false),
            ExternalOperations = table.Column<string>(type: Text(), nullable: false, defaultValue: "[]"),
            PriorSqlAdministrator = table.Column<string>(type: Text(), nullable: true),
            GrantInventory = table.Column<string>(type: Text(), nullable: false, defaultValue: "[]"),
            StartedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            UpdatedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            ActorId = table.Column<string>(type: Text(128), maxLength: 128, nullable: false),
            CorrelationId = table.Column<string>(type: Text(64), maxLength: 64, nullable: false),
            FailureCode = table.Column<string>(type: Text(100), maxLength: 100, nullable: true),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_AdministrationTransitions", x => x.Id);
            table.UniqueConstraint("AK_AdministrationTransitions_Id_CandidateVersion", x => new { x.Id, x.CandidateVersion });
            table.CheckConstraint("CK_AdministrationTransitions_Phase", "Phase IN ('preflight','prepared','sql_switched','replacement_verified','authority_committed','retiring','complete','failed','rollback_pending','rolled_back')");
            table.CheckConstraint("CK_AdministrationTransitions_Version", "CommittedVersion >= 0 AND CandidateVersion > CommittedVersion AND FencingToken > 0");
        });
        migrationBuilder.CreateTable("SystemAdministrationConfigurations", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            EnvironmentKey = table.Column<string>(type: Text(100), maxLength: 100, nullable: false),
            TenantId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            SqlTargetKey = table.Column<string>(type: Text(300), maxLength: 300, nullable: false),
            Mode = table.Column<string>(type: Text(20), maxLength: 20, nullable: false),
            GroupObjectId = table.Column<string>(type: Text(36), maxLength: 36, nullable: true),
            LegacyIndividualObjectId = table.Column<string>(type: Text(36), maxLength: 36, nullable: true),
            SystemRoleGroupMappingId = table.Column<string>(type: Text(36), maxLength: 36, nullable: true),
            InitialOrganizationId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            InitialDepartmentId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            AuthorityVersion = table.Column<int>(type: Integer, nullable: false),
            CommittedTransitionId = table.Column<string>(type: Text(36), maxLength: 36, nullable: true),
            CommittedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            CommittedBy = table.Column<string>(type: Text(128), maxLength: 128, nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_SystemAdministrationConfigurations", x => x.Id);
            table.CheckConstraint("CK_SystemAdministrationConfigurations_Mode", "(Mode = 'group' AND GroupObjectId IS NOT NULL AND LegacyIndividualObjectId IS NULL AND SystemRoleGroupMappingId IS NOT NULL) OR (Mode = 'individual' AND GroupObjectId IS NULL AND LegacyIndividualObjectId IS NOT NULL AND SystemRoleGroupMappingId IS NULL)");
            table.CheckConstraint("CK_SystemAdministrationConfigurations_Version", "AuthorityVersion >= 0");
            table.ForeignKey("FK_SystemAdministrationConfigurations_Organizations_InitialOrganizationId", x => x.InitialOrganizationId, "Organizations", "Id", principalSchema: Schema, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_SystemAdministrationConfigurations_Departments_InitialDepartmentId_InitialOrganizationId",
                x => new { x.InitialDepartmentId, x.InitialOrganizationId }, "Departments", new[] { "Id", "OrganizationId" }, principalSchema: Schema, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_SystemAdministrationConfigurations_RoleGroupMappings_SystemRoleGroupMappingId_TenantId_GroupObjectId",
                x => new { x.SystemRoleGroupMappingId, x.TenantId, x.GroupObjectId }, "RoleGroupMappings", new[] { "Id", "TenantId", "GroupObjectId" }, principalSchema: Schema, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_SystemAdministrationConfigurations_AdministrationTransitions_CommittedTransitionId", x => x.CommittedTransitionId,
                "AdministrationTransitions", "Id", principalSchema: Schema, onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateTable("VerificationReceipts", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            TransitionId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            CandidateVersion = table.Column<int>(type: Integer, nullable: false),
            Mode = table.Column<string>(type: Text(20), maxLength: 20, nullable: false),
            PrincipalObjectId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            TenantId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            MemberObjectId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            CollectionStartedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            CollectedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            EvidenceExpiresAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            Complete = table.Column<bool>(type: Boolean, nullable: false),
            CompositionValidated = table.Column<bool>(type: Boolean, nullable: false),
            AccountEnabled = table.Column<bool>(type: Boolean, nullable: false),
            UserType = table.Column<string>(type: Text(20), maxLength: 20, nullable: false),
            IsActive = table.Column<bool>(type: Boolean, nullable: false),
            Stack = table.Column<string>(type: Text(10), maxLength: 10, nullable: false),
            RuntimeVersion = table.Column<string>(type: Text(100), maxLength: 100, nullable: false),
            CapabilityInventoryVersion = table.Column<string>(type: Text(100), maxLength: 100, nullable: false),
            CapabilityInventoryPassed = table.Column<bool>(type: Boolean, nullable: false),
            SqlTargetKey = table.Column<string>(type: Text(300), maxLength: 300, nullable: false),
            SqlAdministratorObjectId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            SqlBootstrapPassed = table.Column<bool>(type: Boolean, nullable: false),
            VerifiedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            ExpiresAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            AuditEventId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            CorrelationId = table.Column<string>(type: Text(64), maxLength: 64, nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_VerificationReceipts", x => x.Id);
            table.ForeignKey("FK_VerificationReceipts_AdministrationTransitions_TransitionId_CandidateVersion",
                x => new { x.TransitionId, x.CandidateVersion }, "AdministrationTransitions", new[] { "Id", "CandidateVersion" }, principalSchema: Schema, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_VerificationReceipts_ProcessingEvents_AuditEventId", x => x.AuditEventId, "ProcessingEvents", "Id", principalSchema: Schema, onDelete: ReferentialAction.Restrict);
            table.CheckConstraint("CK_VerificationReceipts_Mode", "Mode IN ('group','individual')");
            table.CheckConstraint("CK_VerificationReceipts_Stack", "Stack IN ('stack-a','stack-b','sql')");
            table.CheckConstraint("CK_VerificationReceipts_Eligibility", "Complete = 1 AND CompositionValidated = 1 AND AccountEnabled = 1 AND UserType = 'Member' AND IsActive = 1");
            table.CheckConstraint("CK_VerificationReceipts_Capability", "CapabilityInventoryPassed = 1 AND (Stack <> 'sql' OR SqlBootstrapPassed = 1)");
            table.CheckConstraint("CK_VerificationReceipts_Interval", "CollectionStartedAt <= CollectedAt AND CollectedAt <= VerifiedAt AND VerifiedAt < ExpiresAt AND ExpiresAt <= EvidenceExpiresAt");
            table.CheckConstraint("CK_VerificationReceipts_EvidenceExpiry", SqlServer
                ? "EvidenceExpiresAt <= DATEADD(minute, 5, CollectionStartedAt)"
                : "julianday(EvidenceExpiresAt) <= julianday(CollectionStartedAt) + 5.0 / 1440");
        });
        migrationBuilder.CreateTable("ManagedGrantProvenance", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            PrincipalObjectId = table.Column<string>(type: Text(36), maxLength: 36, nullable: false),
            PrincipalSid = table.Column<string>(type: Text(200), maxLength: 200, nullable: true),
            Resource = table.Column<string>(type: Text(300), maxLength: 300, nullable: false),
            Permission = table.Column<string>(type: Text(100), maxLength: 100, nullable: false),
            ManagementSource = table.Column<string>(type: Text(20), maxLength: 20, nullable: false),
            OriginatingTransitionId = table.Column<string>(type: Text(36), maxLength: 36, nullable: true),
            AdoptingTransitionId = table.Column<string>(type: Text(36), maxLength: 36, nullable: true),
            RetiringTransitionId = table.Column<string>(type: Text(36), maxLength: 36, nullable: true),
            BeforeState = table.Column<string>(type: Text(), nullable: false),
            AfterState = table.Column<string>(type: Text(), nullable: false),
            CreatedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
            UpdatedAt = table.Column<DateTime>(type: Timestamp, nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ManagedGrantProvenance", x => x.Id);
            table.CheckConstraint("CK_ManagedGrantProvenance_Source", "ManagementSource IN ('deployment','application','independent')");
            table.ForeignKey("FK_ManagedGrantProvenance_AdministrationTransitions_OriginatingTransitionId", x => x.OriginatingTransitionId, "AdministrationTransitions", "Id", principalSchema: Schema, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_ManagedGrantProvenance_AdministrationTransitions_AdoptingTransitionId", x => x.AdoptingTransitionId, "AdministrationTransitions", "Id", principalSchema: Schema, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_ManagedGrantProvenance_AdministrationTransitions_RetiringTransitionId", x => x.RetiringTransitionId, "AdministrationTransitions", "Id", principalSchema: Schema, onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.AddForeignKey("FK_RoleAssignments_AdministrationTransitions_ManagedTransitionId", "RoleAssignments",
            "ManagedTransitionId", "AdministrationTransitions", schema: Schema, principalSchema: Schema, principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.CreateIndex("IX_RoleAssignments_ManagedTransitionId", "RoleAssignments", "ManagedTransitionId", Schema);
        migrationBuilder.CreateIndex("IX_RoleAssignments_TenantId_UserObjectId_RoleGroupMappingId", "RoleAssignments",
            new[] { "TenantId", "UserObjectId", "RoleGroupMappingId" }, Schema, unique: true,
            filter: "[Status] = 'active' AND [Source] = 'group' AND [RoleGroupMappingId] IS NOT NULL");
        migrationBuilder.CreateIndex("IX_RoleAssignments_TenantId_UserObjectId_Role_OrganizationId", "RoleAssignments",
            new[] { "TenantId", "UserObjectId", "Role", "OrganizationId" }, Schema, unique: true,
            filter: "[Status] = 'active' AND [Source] = 'delegated' AND [DepartmentId] IS NULL");
        migrationBuilder.CreateIndex("IX_AdministrationTransitions_TargetKey_DesiredFingerprint_CandidateVersion", "AdministrationTransitions",
            new[] { "TargetKey", "DesiredFingerprint", "CandidateVersion" }, Schema, unique: true);
        migrationBuilder.CreateIndex("IX_AdministrationTransitions_TargetKey", "AdministrationTransitions", "TargetKey", Schema, unique: true,
            filter: SqlServer ? "[Phase] IN ('preflight','prepared','sql_switched','replacement_verified','authority_committed','retiring','failed','rollback_pending')" : "[Phase] NOT IN ('complete','rolled_back')");
        migrationBuilder.CreateIndex("IX_SystemAdministrationConfigurations_EnvironmentKey_TenantId_SqlTargetKey", "SystemAdministrationConfigurations",
            new[] { "EnvironmentKey", "TenantId", "SqlTargetKey" }, Schema, unique: true);
        migrationBuilder.CreateIndex("IX_SystemAdministrationConfigurations_InitialOrganizationId", "SystemAdministrationConfigurations", "InitialOrganizationId", Schema);
        migrationBuilder.CreateIndex("IX_SystemAdministrationConfigurations_InitialDepartmentId_InitialOrganizationId", "SystemAdministrationConfigurations", new[] { "InitialDepartmentId", "InitialOrganizationId" }, Schema);
        migrationBuilder.CreateIndex("IX_SystemAdministrationConfigurations_SystemRoleGroupMappingId_TenantId_GroupObjectId", "SystemAdministrationConfigurations", new[] { "SystemRoleGroupMappingId", "TenantId", "GroupObjectId" }, Schema);
        migrationBuilder.CreateIndex("IX_SystemAdministrationConfigurations_CommittedTransitionId", "SystemAdministrationConfigurations", "CommittedTransitionId", Schema);
        migrationBuilder.CreateIndex("IX_VerificationReceipts_TransitionId_CandidateVersion", "VerificationReceipts", new[] { "TransitionId", "CandidateVersion" }, Schema);
        migrationBuilder.CreateIndex("IX_VerificationReceipts_AuditEventId", "VerificationReceipts", "AuditEventId", Schema);
        foreach (var column in new[] { "OriginatingTransitionId", "AdoptingTransitionId", "RetiringTransitionId" })
            migrationBuilder.CreateIndex($"IX_ManagedGrantProvenance_{column}", "ManagedGrantProvenance", column, Schema);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Administration rollback is a new verified transition; authority and audit history must not be dropped.");
}
