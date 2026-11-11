using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Infrastructure.Persistence;

public partial class AppDbContext
{
    private void ConfigureAdministration(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdministrationTransition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.HasAlternateKey(x => new { x.Id, x.CandidateVersion });
            e.Property(x => x.TargetKey).HasMaxLength(300);
            e.Property(x => x.DesiredFingerprint).HasMaxLength(64);
            e.Property(x => x.Phase).HasMaxLength(32);
            e.Property(x => x.LeaseOwner).HasMaxLength(128);
            e.Property(x => x.ActorId).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.FailureCode).HasMaxLength(100);
            e.Property(x => x.SupersedesTransitionId).HasMaxLength(36);
            e.Property(x => x.SupersededByTransitionId).HasMaxLength(36);
            e.Property(x => x.FencingToken).IsConcurrencyToken();
            e.Property(x => x.ExternalOperations).HasDefaultValue("[]");
            e.Property(x => x.GrantInventory).HasDefaultValue("[]");
            e.HasIndex(x => new { x.TargetKey, x.DesiredFingerprint, x.CandidateVersion }).IsUnique();
            e.HasIndex(x => x.TargetKey).IsUnique().HasFilter(Database.IsSqlServer()
                ? "[Phase] IN ('preflight','prepared','sql_switched','replacement_verified','authority_committed','retiring','failed','rollback_pending') AND [SupersededByTransitionId] IS NULL"
                : "[Phase] NOT IN ('complete','rolled_back') AND [SupersededByTransitionId] IS NULL");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_AdministrationTransitions_Phase", "Phase IN ('preflight','prepared','sql_switched','replacement_verified','authority_committed','retiring','complete','failed','rollback_pending','rolled_back')");
                t.HasCheckConstraint("CK_AdministrationTransitions_Version", "CommittedVersion >= 0 AND CandidateVersion > CommittedVersion AND FencingToken > 0");
            });
        });
        modelBuilder.Entity<SystemAdministrationConfiguration>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.SystemRoleGroupMappingId).HasMaxLength(36);
            e.Property(x => x.InitialOrganizationId).HasMaxLength(36);
            e.Property(x => x.InitialDepartmentId).HasMaxLength(36);
            e.Property(x => x.CommittedTransitionId).HasMaxLength(36);
            e.Property(x => x.EnvironmentKey).HasMaxLength(100);
            e.Property(x => x.TenantId).HasMaxLength(36);
            e.Property(x => x.SqlTargetKey).HasMaxLength(300);
            e.Property(x => x.Mode).HasMaxLength(20);
            e.Property(x => x.GroupObjectId).HasMaxLength(36);
            e.Property(x => x.LegacyIndividualObjectId).HasMaxLength(36);
            e.Property(x => x.CommittedBy).HasMaxLength(128);
            e.Property(x => x.AuthorityVersion).IsConcurrencyToken();
            e.HasIndex(x => new { x.EnvironmentKey, x.TenantId, x.SqlTargetKey }).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.InitialOrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Department>().WithMany()
                .HasForeignKey(x => new { x.InitialDepartmentId, x.InitialOrganizationId })
                .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RoleGroupMapping>().WithMany()
                .HasForeignKey(x => new { x.SystemRoleGroupMappingId, x.TenantId, x.GroupObjectId })
                .HasPrincipalKey(x => new { x.Id, x.TenantId, x.GroupObjectId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AdministrationTransition>().WithMany().HasForeignKey(x => x.CommittedTransitionId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SystemAdministrationConfigurations_Mode", "(Mode = 'group' AND GroupObjectId IS NOT NULL AND LegacyIndividualObjectId IS NULL AND SystemRoleGroupMappingId IS NOT NULL) OR (Mode = 'individual' AND GroupObjectId IS NULL AND LegacyIndividualObjectId IS NOT NULL AND SystemRoleGroupMappingId IS NULL)");
                t.HasCheckConstraint("CK_SystemAdministrationConfigurations_Version", "AuthorityVersion >= 0");
            });
        });
        modelBuilder.Entity<VerificationReceipt>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.TransitionId).HasMaxLength(36);
            e.Property(x => x.AuditEventId).HasMaxLength(36);
            e.Property(x => x.Mode).HasMaxLength(20);
            e.Property(x => x.PrincipalObjectId).HasMaxLength(36);
            e.Property(x => x.TenantId).HasMaxLength(36);
            e.Property(x => x.MemberObjectId).HasMaxLength(36);
            e.Property(x => x.UserType).HasMaxLength(20);
            e.Property(x => x.Stack).HasMaxLength(10);
            e.Property(x => x.RuntimeVersion).HasMaxLength(100);
            e.Property(x => x.CapabilityInventoryVersion).HasMaxLength(100);
            e.Property(x => x.SqlTargetKey).HasMaxLength(300);
            e.Property(x => x.SqlAdministratorObjectId).HasMaxLength(36);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasOne<AdministrationTransition>().WithMany()
                .HasForeignKey(x => new { x.TransitionId, x.CandidateVersion })
                .HasPrincipalKey(x => new { x.Id, x.CandidateVersion }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProcessingEvent>().WithMany().HasForeignKey(x => x.AuditEventId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_VerificationReceipts_Mode", "Mode IN ('group','individual')");
                t.HasCheckConstraint("CK_VerificationReceipts_Stack", "Stack IN ('stack-a','stack-b','sql')");
                t.HasCheckConstraint("CK_VerificationReceipts_Eligibility", "Complete = 1 AND CompositionValidated = 1 AND AccountEnabled = 1 AND UserType = 'Member' AND IsActive = 1");
                t.HasCheckConstraint("CK_VerificationReceipts_Capability", "CapabilityInventoryPassed = 1 AND (Stack <> 'sql' OR SqlBootstrapPassed = 1)");
                t.HasCheckConstraint("CK_VerificationReceipts_Interval", "CollectionStartedAt <= CollectedAt AND CollectedAt <= VerifiedAt AND VerifiedAt < ExpiresAt AND ExpiresAt <= EvidenceExpiresAt");
                t.HasCheckConstraint("CK_VerificationReceipts_EvidenceExpiry", Database.IsSqlServer()
                    ? "EvidenceExpiresAt <= DATEADD(minute, 5, CollectionStartedAt)"
                    : "julianday(EvidenceExpiresAt) <= julianday(CollectionStartedAt) + 5.0 / 1440");
            });
        });
        modelBuilder.Entity<ManagedGrantProvenance>(e =>
        {
            e.ToTable("ManagedGrantProvenance");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.OriginatingTransitionId).HasMaxLength(36);
            e.Property(x => x.AdoptingTransitionId).HasMaxLength(36);
            e.Property(x => x.RetiringTransitionId).HasMaxLength(36);
            e.Property(x => x.PrincipalObjectId).HasMaxLength(36);
            e.Property(x => x.PrincipalSid).HasMaxLength(200);
            e.Property(x => x.Resource).HasMaxLength(300);
            e.Property(x => x.Permission).HasMaxLength(100);
            e.Property(x => x.ManagementSource).HasMaxLength(20);
            e.HasOne<AdministrationTransition>().WithMany().HasForeignKey(x => x.OriginatingTransitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AdministrationTransition>().WithMany().HasForeignKey(x => x.AdoptingTransitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AdministrationTransition>().WithMany().HasForeignKey(x => x.RetiringTransitionId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_ManagedGrantProvenance_Source", "ManagementSource IN ('deployment','application','independent')"));
        });
    }
}
