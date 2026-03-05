using Microsoft.EntityFrameworkCore;
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
    public DbSet<ScoringRun> ScoringRuns => Set<ScoringRun>();
    public DbSet<AggregatedResult> AggregatedResults => Set<AggregatedResult>();
    public DbSet<ExtractionArtifact> ExtractionArtifacts => Set<ExtractionArtifact>();
    public DbSet<ManualReviewData> ManualReviews => Set<ManualReviewData>();
    public DbSet<FailureQueueItem> FailureQueueItems => Set<FailureQueueItem>();
    public DbSet<ProcessingEvent> ProcessingEvents => Set<ProcessingEvent>();
    public DbSet<PasswordResetRequest> PasswordResetRequests => Set<PasswordResetRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
        });

        // Job
        modelBuilder.Entity<Job>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Department).HasMaxLength(100).IsRequired();
            e.Property(x => x.Organisation).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasMany(x => x.ConfigVersions).WithOne(x => x.Job).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Applications).WithOne(x => x.Job).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        // JobConfigVersion
        modelBuilder.Entity<JobConfigVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AggregationStrategy).HasMaxLength(20);
        });

        // Application
        modelBuilder.Entity<TalentMatch.Domain.Entities.Application>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.FinalDecision).HasMaxLength(30);
            e.HasMany(x => x.Documents).WithOne(x => x.Application).HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ScoringRuns).WithOne(x => x.Application).HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AggregatedResult).WithOne(x => x.Application).HasForeignKey<AggregatedResult>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ManualReview).WithOne(x => x.Application).HasForeignKey<ManualReviewData>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Extraction).WithOne(x => x.Application).HasForeignKey<ExtractionArtifact>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        // ApplicationDocument
        modelBuilder.Entity<ApplicationDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.FileName).HasMaxLength(500);
        });

        // ScoringRun
        modelBuilder.Entity<ScoringRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AiModelId).HasMaxLength(100);
        });

        // AggregatedResult
        modelBuilder.Entity<AggregatedResult>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ApplicationId).IsUnique();
            e.Property(x => x.Decision).HasMaxLength(30);
        });

        // ExtractionArtifact
        modelBuilder.Entity<ExtractionArtifact>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ApplicationId).IsUnique();
            e.Property(x => x.Status).HasMaxLength(20);
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
            e.Property(x => x.EventType).HasMaxLength(50);
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.HasIndex(x => x.Timestamp);
        });

        // PasswordResetRequest
        modelBuilder.Entity<PasswordResetRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(20);
        });
    }
}
