using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Infrastructure.Persistence;

internal sealed class WireEnumConverter<T>() : ValueConverter<T, string>(
    v => v.ToWire(),
    s => WireEnum.Parse<T>(s)) where T : struct, Enum;

internal static class BulkScoringModelConfiguration
{
    private static string In<T>() where T : struct, Enum =>
        string.Join(", ", WireEnum.AllWireValues<T>().Select(v => $"'{v}'"));

    public static void ConfigureBulkScoring(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BulkAuditEvent>(e =>
        {
            e.ToTable("BulkAuditEvents", t =>
            {
                t.HasCheckConstraint(
                    "CK_BulkAuditEvents_ActorType",
                    $"ActorType IN ({In<BulkAuditActorType>()})");
                t.HasCheckConstraint(
                    "CK_BulkAuditEvents_Boundary",
                    $"Boundary IN ({In<BulkAuditBoundary>()})");
                t.HasCheckConstraint("CK_BulkAuditEvents_SchemaVersion", "SchemaVersion > 0");
            });
            e.HasKey(x => x.AuditEventId);
            e.Property(x => x.AuditEventId).HasMaxLength(36).ValueGeneratedNever();
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(36).IsRequired();
            e.Property(x => x.ActorType).HasConversion(new WireEnumConverter<BulkAuditActorType>())
                .HasMaxLength(20).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(128).IsRequired();
            e.Property(x => x.InitiatedByActorId).HasMaxLength(128);
            e.Property(x => x.OccurredAt).HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            e.Property(x => x.SubjectType).HasMaxLength(100).IsRequired();
            e.Property(x => x.SubjectId).HasMaxLength(128).IsRequired();
            e.Property(x => x.SafeFactsJson).IsRequired();
            e.Property(x => x.Boundary).HasConversion(new WireEnumConverter<BulkAuditBoundary>())
                .HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.SubjectType, x.SubjectId, x.OccurredAt });
            e.HasIndex(x => new { x.SubjectId, x.EventType });
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<BulkSourceAdmissionEvent>(e =>
        {
            e.ToTable("BulkSourceAdmissionEvents", t =>
            {
                t.HasCheckConstraint("CK_BulkSourceAdmissionEvents_Kind", $"Kind IN ({In<BulkSourceAdmissionKind>()})");
                t.HasCheckConstraint("CK_BulkSourceAdmissionEvents_Expiry", "ExpiresAt > OccurredAt");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.Kind).HasConversion(new WireEnumConverter<BulkSourceAdmissionKind>()).HasMaxLength(32).IsRequired();
            e.Property(x => x.SubjectHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => new { x.Kind, x.SubjectHash, x.ExpiresAt });
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<BulkSourceConnection>(e =>
        {
            e.ToTable("BulkSourceConnections", t =>
            {
                t.HasCheckConstraint("CK_BulkSourceConnections_Status", $"Status IN ({In<BulkSourceConnectionStatus>()})");
                t.HasCheckConstraint("CK_BulkSourceConnections_SourceKind", $"SourceKind IS NULL OR SourceKind IN ({In<BulkSourceKind>()})");
                t.HasCheckConstraint("CK_BulkSourceConnections_Expiry", "ExpiresAt >= LastActivityAt AND ExpiresAt >= ObtainedAt");
                t.HasCheckConstraint("CK_BulkSourceConnections_Consumed",
                    "(Status <> 'consumed') OR (ConsumedByRequestId IS NOT NULL AND PendingFolderUrlEncrypted IS NULL AND StateHash IS NULL AND PkceVerifierEncrypted IS NULL AND ProtectedPayload IS NULL AND WrappedDataKey IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.ProductActorId).HasMaxLength(128).IsRequired();
            e.Property(x => x.JobId).HasMaxLength(36).IsRequired();
            e.Property(x => x.Status).HasConversion(new WireEnumConverter<BulkSourceConnectionStatus>()).HasMaxLength(20).IsRequired();
            e.Property(x => x.CanonicalUrlHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.StateHash).HasMaxLength(128);
            e.Property(x => x.RedirectAfterPath).HasMaxLength(2048).IsRequired();
            e.Property(x => x.SourceTenantId).HasMaxLength(64);
            e.Property(x => x.SourceAccountBindingHash).HasMaxLength(128);
            e.Property(x => x.SourceAccountDisplay).HasMaxLength(256);
            e.Property(x => x.SourceKind).HasConversion(new WireEnumConverter<BulkSourceKind>()).HasMaxLength(20);
            e.Property(x => x.SourceCloud).HasMaxLength(20);
            e.Property(x => x.SourceDriveId).HasMaxLength(200);
            e.Property(x => x.SourceFolderItemId).HasMaxLength(200);
            e.Property(x => x.SourceFolderName).HasMaxLength(500);
            e.Property(x => x.SourceFolderPath).HasMaxLength(2000);
            e.Property(x => x.SourceWebUrl).HasMaxLength(2048);
            e.Property(x => x.KeyVersion).HasMaxLength(200);
            e.Property(x => x.ConsumedByRequestId).HasMaxLength(36);
            e.Property(x => x.FailureCode).HasMaxLength(100);
            e.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.StateHash).IsUnique().HasFilter("[StateHash] IS NOT NULL");
            e.HasIndex(x => new { x.ProductActorId, x.JobId, x.Status, x.ExpiresAt });
        });

        modelBuilder.Entity<BulkScoringRequest>(e =>
        {
            e.ToTable("BulkScoringRequests", t =>
            {
                t.HasCheckConstraint("CK_BulkScoringRequests_Status", $"Status IN ({In<BulkRequestStatus>()})");
                t.HasCheckConstraint("CK_BulkScoringRequests_DiscoveryStatus", $"DiscoveryStatus IN ({In<BulkDiscoveryStatus>()})");
                t.HasCheckConstraint("CK_BulkScoringRequests_Outcome", $"Outcome IS NULL OR Outcome IN ({In<BulkRequestOutcome>()})");
                t.HasCheckConstraint("CK_BulkScoringRequests_OutcomeIffCompleted", "(Outcome IS NULL AND Status <> 'completed') OR (Outcome IS NOT NULL AND Status = 'completed')");
                t.HasCheckConstraint("CK_BulkScoringRequests_SourceKind", $"SourceKind IN ({In<BulkSourceKind>()})");
                t.HasCheckConstraint("CK_BulkScoringRequests_Counts", "DiscoveredCount >= 0 AND ExcludedCount >= 0 AND InaccessibleCount >= 0 AND WaitingCount >= 0 AND ImportingCount >= 0 AND ConvertingCount >= 0 AND ScoringCount >= 0 AND CompletedCount >= 0 AND FailedCount >= 0 AND SkippedCount >= 0 AND CancelledCount >= 0 AND TotalEligibleBytes >= 0 AND EntriesScanned >= 0 AND DiscoveryStepCount >= 0");
                t.HasCheckConstraint("CK_BulkScoringRequests_FrozenLimits", "MaxFilesPerRequest BETWEEN 1 AND 100000 AND MaxBytesPerFile > 0 AND MaxTotalBytes BETWEEN 1048576 AND 1099511627776 AND DiscoveryStepMaxFiles BETWEEN 1 AND 1000 AND DiscoveryStepMaxEntries > 0 AND DiscoveryStepMaxSeconds > 0");
                t.HasCheckConstraint("CK_BulkScoringRequests_IncludedLimits", "IncludedItemCount >= 0 AND IncludedItemCount <= MaxFilesPerRequest AND IncludedBytes >= 0 AND IncludedBytes <= MaxTotalBytes");
                t.HasCheckConstraint("CK_BulkScoringRequests_Acknowledgment", "(AcknowledgedAt IS NULL AND AcknowledgedByActorId IS NULL) OR (AcknowledgedAt IS NOT NULL AND AcknowledgedByActorId = OwnerActorId)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.JobId).HasMaxLength(36).IsRequired();
            e.Property(x => x.OwnerActorId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Status).HasConversion(new WireEnumConverter<BulkRequestStatus>()).HasMaxLength(20).IsRequired();
            e.Property(x => x.Outcome).HasConversion(new WireEnumConverter<BulkRequestOutcome>()).HasMaxLength(20);
            e.Property(x => x.SourceKind).HasConversion(new WireEnumConverter<BulkSourceKind>()).HasMaxLength(20).IsRequired();
            e.Property(x => x.SourceTenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceAccountBindingHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceDriveId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceFolderItemId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceFolderName).HasMaxLength(500).IsRequired();
            e.Property(x => x.SourceFolderPath).HasMaxLength(2000).IsRequired();
            e.Property(x => x.SourceWebUrl).HasMaxLength(2048).IsRequired();
            e.Property(x => x.CapturedScoringConfigVersionId).HasMaxLength(36);
            e.Property(x => x.DiscoveryStatus).HasConversion(new WireEnumConverter<BulkDiscoveryStatus>()).HasMaxLength(20).IsRequired();
            e.Property(x => x.ConfirmedAt).HasConversion(
                value => value,
                value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);
            e.Property(x => x.CancelRequestedAt).HasConversion(
                value => value,
                value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);
            e.Property(x => x.TerminalAt).HasConversion(
                value => value,
                value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);
            e.Property(x => x.AcknowledgedAt).HasConversion(
                value => value,
                value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);
            e.Property(x => x.ExcludedByReasonJson).IsRequired();
            e.Property(x => x.RetryOfRequestId).HasMaxLength(36);
            e.Property(x => x.CancelledByActorId).HasMaxLength(128);
            e.Property(x => x.AcknowledgedByActorId).HasMaxLength(128);
            e.Property(x => x.ConnectionId).HasMaxLength(36);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BulkScoringRequest>().WithMany().HasForeignKey(x => x.RetryOfRequestId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Items).WithOne(x => x.Request).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Authorization).WithOne(x => x.Request).HasForeignKey<BulkRequestAuthorization>(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.JobId, x.CreatedAt });
            e.HasIndex(x => new { x.OwnerActorId, x.Status, x.AcknowledgedAt });
            e.HasIndex(x => new { x.Status, x.TerminalAt });
            e.HasIndex(x => x.ConnectionId).IsUnique().HasFilter("[ConnectionId] IS NOT NULL");
        });

        modelBuilder.Entity<BulkScoringItem>(e =>
        {
            e.ToTable("BulkScoringItems", t =>
            {
                t.HasCheckConstraint("CK_BulkScoringItems_Status", $"Status IN ({In<BulkItemStatus>()})");
                t.HasCheckConstraint("CK_BulkScoringItems_Reason", $"ReasonCode IS NULL OR ReasonCode IN ({In<BulkReasonCode>()})");
                t.HasCheckConstraint("CK_BulkScoringItems_Sizes", "SizeBytes >= 0 AND Attempt >= 0");
                t.HasCheckConstraint("CK_BulkScoringItems_ScoredVersion", "ScoredVersion IS NULL OR ScoredVersion IN ('original', 'converted')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.RequestId).HasMaxLength(36).IsRequired();
            e.Property(x => x.SourceDriveId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceDriveItemId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceETag).HasMaxLength(200).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.SourcePath).HasMaxLength(2000).IsRequired();
            e.Property(x => x.MimeType).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion(new WireEnumConverter<BulkItemStatus>()).HasMaxLength(20).IsRequired();
            e.Property(x => x.ReasonCode).HasConversion(new WireEnumConverter<BulkReasonCode>()).HasMaxLength(40);
            e.Property(x => x.ReasonMessage).HasMaxLength(1000);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.ApplicationId).HasMaxLength(36);
            e.Property(x => x.DocumentId).HasMaxLength(36);
            e.Property(x => x.ScoredVersion).HasConversion(new WireEnumConverter<BulkScoredVersion>()).HasMaxLength(20);
            e.Property(x => x.MinimumPageSsim).HasPrecision(6, 5);
            e.Property(x => x.ScoringConfigVersionId).HasMaxLength(36);
            e.Property(x => x.LeaseOwner).HasMaxLength(100);
            e.Property(x => x.RetryOfItemId).HasMaxLength(36);
            e.Ignore(x => x.HasRetainedOriginal);
            e.HasOne<BulkScoringItem>().WithMany().HasForeignKey(x => x.RetryOfItemId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Contents).WithOne(x => x.Item).HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.Status });
            e.HasIndex(x => new { x.RequestId, x.Ordinal });
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("[Status] IN ('waiting', 'importing', 'converting')");
            e.HasIndex(x => x.Status).HasDatabaseName("IX_BulkScoringItems_Scoring").HasFilter("[Status] = 'scoring'");
            e.HasIndex(x => new { x.RequestId, x.SourceDriveItemId }).IsUnique();
            e.HasIndex(x => new { x.RequestId, x.Fingerprint }).IsUnique().HasDatabaseName("UX_BulkScoringItems_Request_Fingerprint").HasFilter("[FingerprintClaimed] = 1");
        });

        modelBuilder.Entity<BulkScoringItemContent>(e =>
        {
            e.ToTable("BulkScoringItemContents", t =>
            {
                t.HasCheckConstraint("CK_BulkScoringItemContents_Kind", $"Kind IN ({In<BulkContentKind>()})");
                t.HasCheckConstraint("CK_BulkScoringItemContents_OneLocation", "(BlobUri IS NOT NULL AND ContentBase64 IS NULL) OR (BlobUri IS NULL AND ContentBase64 IS NOT NULL)");
                t.HasCheckConstraint("CK_BulkScoringItemContents_Size", "SizeBytes >= 0");
            });
            e.HasKey(x => new { x.ItemId, x.Kind });
            e.Property(x => x.ItemId).HasMaxLength(36);
            e.Property(x => x.Kind).HasConversion(new WireEnumConverter<BulkContentKind>()).HasMaxLength(20);
            e.Property(x => x.BlobUri).HasMaxLength(2048);
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<BulkImportSettings>(e =>
        {
            e.ToTable("BulkImportSettings", t =>
            {
                t.HasCheckConstraint("CK_BulkImportSettings_Singleton", "Id = 1");
                t.HasCheckConstraint("CK_BulkImportSettings_MaxFiles", "MaxFilesPerRequest > 0");
                t.HasCheckConstraint("CK_BulkImportSettings_MaxFilesV3", "MaxFilesPerRequest <= 100000");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UpdatedByActorId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<BulkRequestAuthorization>(e =>
        {
            e.ToTable("BulkRequestAuthorizations", t =>
            {
                t.HasCheckConstraint("CK_BulkRequestAuthorizations_Status", $"Status IN ({In<BulkAuthorizationStatus>()})");
                t.HasCheckConstraint("CK_BulkRequestAuthorizations_NoMaterialWhenInactive", "Status = 'active' OR (Payload IS NULL AND WrappedDataKey IS NULL)");
            });
            e.HasKey(x => x.RequestId);
            e.Property(x => x.RequestId).HasMaxLength(36);
            e.Property(x => x.ProductActorId).HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceTenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceAccountBindingHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceDriveId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceFolderItemId).HasMaxLength(200).IsRequired();
            e.Property(x => x.KeyVersion).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion(new WireEnumConverter<BulkAuthorizationStatus>()).HasMaxLength(20).IsRequired();
            e.Property(x => x.LapseReason).HasMaxLength(100);
            e.HasIndex(x => new { x.Status, x.ObtainedAt });
        });
    }
}
