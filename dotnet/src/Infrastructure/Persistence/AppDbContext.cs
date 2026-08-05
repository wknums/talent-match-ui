using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobConfigVersion> JobConfigVersions => Set<JobConfigVersion>();
    public DbSet<TalentMatch.Domain.Entities.Application> Applications => Set<TalentMatch.Domain.Entities.Application>();
    public DbSet<ApplicationDocument> ApplicationDocuments => Set<ApplicationDocument>();
    public DbSet<DocumentBlob> DocumentBlobs => Set<DocumentBlob>();
    public DbSet<ScoringRun> ScoringRuns => Set<ScoringRun>();
    public DbSet<AggregatedResult> AggregatedResults => Set<AggregatedResult>();
    public DbSet<ExtractionArtifact> ExtractionArtifacts => Set<ExtractionArtifact>();
    public DbSet<ManualReviewData> ManualReviews => Set<ManualReviewData>();
    public DbSet<FailureQueueItem> FailureQueueItems => Set<FailureQueueItem>();
    public DbSet<ProcessingEvent> ProcessingEvents => Set<ProcessingEvent>();
    public DbSet<PasswordResetRequest> PasswordResetRequests => Set<PasswordResetRequest>();
    public DbSet<ScoringPrompt> ScoringPrompts => Set<ScoringPrompt>();
    public DbSet<PromptTestRun> PromptTestRuns => Set<PromptTestRun>();
    public DbSet<ScoringBatch> ScoringBatches => Set<ScoringBatch>();
    public DbSet<ScoringJobProgress> ScoringJobProgress => Set<ScoringJobProgress>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<DepartmentMembership> DepartmentMemberships => Set<DepartmentMembership>();
    public DbSet<RoleGroupMapping> RoleGroupMappings => Set<RoleGroupMapping>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Azure SQL: isolate all tables under the 'talentmatch' schema.
        // SQLite does not support schemas, so this is a no-op for local dev.
        if (Database.IsSqlServer())
            modelBuilder.HasDefaultSchema("talentmatch");

        // User
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique()
                .HasFilter("[AuthenticationProvider] = 'simple'");
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.AuthenticationProvider).HasMaxLength(20).HasDefaultValue("simple").IsRequired();
            e.Property(x => x.EntraTenantId).HasMaxLength(36);
            e.Property(x => x.EntraObjectId).HasMaxLength(36);
            e.Property(x => x.PasswordHash).HasMaxLength(128);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.AuthorizationVersion).HasDefaultValue(0).IsConcurrencyToken();
            e.HasIndex(x => new { x.EntraTenantId, x.EntraObjectId }).IsUnique()
                .HasFilter("[AuthenticationProvider] = 'entra'");
            e.ToTable(t => t.HasCheckConstraint("CK_Users_IdentityProvider", "(AuthenticationProvider = 'simple' AND PasswordHash IS NOT NULL AND EntraTenantId IS NULL AND EntraObjectId IS NULL) OR (AuthenticationProvider = 'entra' AND PasswordHash IS NULL AND EntraTenantId IS NOT NULL AND EntraObjectId IS NOT NULL AND PasswordResetRequired = 0)"));
        });

        modelBuilder.Entity<Organization>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("active").IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique().HasFilter("[Status] = 'active'");
            e.ToTable(t => t.HasCheckConstraint("CK_Organizations_Status", "Status IN ('active', 'retired')"));
        });

        modelBuilder.Entity<Department>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.Id, x.OrganizationId });
            e.Property(x => x.OrganizationId).HasMaxLength(36).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("active").IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Organization).WithMany(x => x.Departments).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[Status] = 'active'");
            e.ToTable(t => t.HasCheckConstraint("CK_Departments_Status", "Status IN ('active', 'retired')"));
        });

        modelBuilder.Entity<OrganizationMembership>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("active").IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.OrganizationMemberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Organization).WithMany(x => x.Memberships).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DefaultDepartmentMembership).WithMany(x => x.DefaultForOrganizationMemberships)
                .HasForeignKey(x => new { x.DefaultDepartmentMembershipId, x.UserId, x.OrganizationId })
                .HasPrincipalKey(x => new { x.Id, x.UserId, x.OrganizationId })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.OrganizationId }).IsUnique().HasFilter("[Status] = 'active'");
            e.ToTable(t => t.HasCheckConstraint("CK_OrganizationMemberships_Status", "(Status = 'active' AND RevokedAt IS NULL AND DefaultDepartmentMembershipId IS NOT NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)"));
        });

        modelBuilder.Entity<DepartmentMembership>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.Id, x.UserId, x.OrganizationId });
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("active").IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.DepartmentMemberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany(x => x.Memberships)
                .HasForeignKey(x => new { x.DepartmentId, x.OrganizationId })
                .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.DepartmentId }).IsUnique().HasFilter("[Status] = 'active'");
            e.ToTable(t => t.HasCheckConstraint("CK_DepartmentMemberships_Status", "(Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)"));
        });

        modelBuilder.Entity<RoleGroupMapping>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(36).IsRequired();
            e.Property(x => x.GroupObjectId).HasMaxLength(36).IsRequired();
            e.Property(x => x.Role).HasMaxLength(30).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.GroupObjectId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_RoleGroupMappings_Scope", "(Role = 'admin' AND OrganizationId IS NULL AND DepartmentId IS NULL) OR (Role = 'organization_admin' AND OrganizationId IS NOT NULL AND DepartmentId IS NULL) OR (Role = 'recruiter' AND OrganizationId IS NOT NULL AND DepartmentId IS NOT NULL) OR (Role = 'business_panel' AND OrganizationId IS NOT NULL)"));
        });

        modelBuilder.Entity<RoleAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(36).IsRequired();
            e.Property(x => x.UserObjectId).HasMaxLength(36).IsRequired();
            e.Property(x => x.Role).HasMaxLength(30).IsRequired();
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("active").IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.RoleAssignments).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.RoleGroupMapping).WithMany().HasForeignKey(x => x.RoleGroupMappingId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.UserObjectId, x.RoleGroupMappingId }).IsUnique().HasFilter("[Status] = 'active' AND [RoleGroupMappingId] IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.UserObjectId, x.Role, x.OrganizationId, x.DepartmentId }).IsUnique()
                .HasFilter("[Status] = 'active' AND [Source] = 'delegated'");
            e.ToTable(t => t.HasCheckConstraint("CK_RoleAssignments_Status", "(Status = 'active' AND RevokedAt IS NULL) OR (Status = 'revoked' AND RevokedAt IS NOT NULL)"));
        });

        // Job
        modelBuilder.Entity<Job>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Department).HasMaxLength(100).IsRequired();
            e.Property(x => x.Organisation).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.OrganizationId).HasMaxLength(36);
            e.Property(x => x.DepartmentId).HasMaxLength(36);
            e.HasMany(x => x.ConfigVersions).WithOne(x => x.Job).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Applications).WithOne(x => x.Job).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DepartmentEntity).WithMany()
                .HasForeignKey(x => new { x.DepartmentId, x.OrganizationId })
                .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.DepartmentId });
        });

        // JobConfigVersion
        modelBuilder.Entity<JobConfigVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AggregationStrategy).HasMaxLength(20);
            e.Ignore(x => x.MustHaveCriteriaJson);
            e.Property(x => x.MustHavesJson).HasColumnName("MustHavesJson");
            e.Property(x => x.RunsPerApplication).HasColumnName("RunsPerApplication");
        });

        // Application
        modelBuilder.Entity<TalentMatch.Domain.Entities.Application>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CandidateRef).HasMaxLength(100).HasDefaultValue(string.Empty);
            e.Property(x => x.CandidateName).HasMaxLength(200);
            e.Property(x => x.CandidateEmail).HasMaxLength(320);
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.FinalDecision).HasMaxLength(30);
            e.HasMany(x => x.Documents).WithOne(x => x.Application).HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ScoringRuns).WithOne(x => x.Application).HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AggregatedResult).WithOne(x => x.Application).HasForeignKey<AggregatedResult>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ManualReview).WithOne(x => x.Application).HasForeignKey<ManualReviewData>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Extraction).WithOne(x => x.Application).HasForeignKey<ExtractionArtifact>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TestRunId);
        });

        // ApplicationDocument
        modelBuilder.Entity<ApplicationDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.FileName).HasMaxLength(500);
            e.Property(x => x.FileType).HasColumnName("MimeType");
            e.Property(x => x.FileSize).HasColumnName("SizeBytes");
            e.Property(x => x.UploadTimestamp)
                .HasColumnName("UploadedAt")
                .HasConversion(new ValueConverter<DateTime?, string?>(
                    value => value.HasValue ? value.Value.ToString("O") : null,
                    value => string.IsNullOrWhiteSpace(value) ? null : DateTime.Parse(value)));
            e.Ignore(x => x.ContentBase64);
        });

        modelBuilder.Entity<DocumentBlob>(e =>
        {
            e.HasKey(x => x.DocumentId);
            e.HasOne<ApplicationDocument>()
                .WithOne()
                .HasForeignKey<DocumentBlob>(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ScoringRun
        modelBuilder.Entity<ScoringRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TotalScore).HasColumnName("OverallScore");
            e.Property(x => x.CategoryScoresJson).HasColumnName("SubScoresJson");
            e.Property(x => x.MustHaveEvaluationJson).HasColumnName("MustHaveResultJson");
            e.Property(x => x.ImprovementTipsJson).HasColumnName("ImprovementRecsJson");
            e.Property(x => x.AiModelId).HasColumnName("ModelDeploymentId").HasMaxLength(100);
            e.Property(x => x.PromptVersion).HasColumnName("PromptVersionId");
            e.Ignore(x => x.InputTokens);
            e.Ignore(x => x.OutputTokens);
        });

        // AggregatedResult
        modelBuilder.Entity<AggregatedResult>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ApplicationId).IsUnique();
            e.Property(x => x.Decision).HasColumnName("FinalDecision").HasMaxLength(30);
            e.Property(x => x.ConsolidatedRationale).HasColumnName("RationaleText");
            e.Property(x => x.MergedImprovementTipsJson).HasColumnName("RecommendationsText");
        });

        // ExtractionArtifact
        modelBuilder.Entity<ExtractionArtifact>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ApplicationId).IsUnique();
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.NormalisedText).HasColumnName("Markdown");
            e.Property(x => x.ConfidenceScore).HasColumnName("Confidence");
        });

        // ManualReviewData
        modelBuilder.Entity<ManualReviewData>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ApplicationId).IsUnique();
        });

        // FailureQueueItem
        modelBuilder.Entity<FailureQueueItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasMaxLength(50);
        });

        // ProcessingEvent
        modelBuilder.Entity<ProcessingEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasColumnName("Action").HasMaxLength(50);
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.Property(x => x.PayloadJson).HasColumnName("DetailsJson");
            e.HasIndex(x => x.Timestamp);
        });

        // PasswordResetRequest
        modelBuilder.Entity<PasswordResetRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(20);
        });

        // ScoringPrompt
        modelBuilder.Entity<ScoringPrompt>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PromptText).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.Author).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.JobId, x.Status });
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.TestRuns).WithOne(x => x.Prompt).HasForeignKey(x => x.PromptId).OnDelete(DeleteBehavior.Cascade);
        });

        // PromptTestRun
        modelBuilder.Entity<PromptTestRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.HasIndex(x => x.PromptId);
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.NoAction);
        });

        // ScoringBatch
        modelBuilder.Entity<ScoringBatch>(e =>
        {
            e.ToTable("ScoringBatches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("BatchId").HasMaxLength(36);
            e.Property(x => x.JobId).HasMaxLength(36).IsRequired();
            e.Property(x => x.PromptVersionId).HasMaxLength(36).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.SubmissionId).HasMaxLength(128);
            e.Property(x => x.PollUrl).HasMaxLength(512);
            e.Property(x => x.LeaseOwner).HasMaxLength(128);
            e.HasIndex(x => x.JobId).HasDatabaseName("IX_ScoringBatches_JobId");
            e.HasIndex(x => new { x.Status, x.NextPollAt }).HasDatabaseName("IX_ScoringBatches_Status_NextPollAt");
            e.HasIndex(x => x.SubmissionId).IsUnique().HasFilter("[SubmissionId] IS NOT NULL").HasDatabaseName("UX_ScoringBatches_SubmissionId");
        });

        // ScoringJobProgress
        modelBuilder.Entity<ScoringJobProgress>(e =>
        {
            e.ToTable("ScoringJobProgress");
            e.HasKey(x => x.JobId);
            e.Property(x => x.JobId).HasMaxLength(36);
        });
    }
}
