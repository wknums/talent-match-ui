using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

// Builds the real access helper and request shapes for handler tests, with every collaborator replaceable.
internal static class BulkHandlerKit
{
    public const string UserId = "11111111-1111-4111-8111-111111111111";
    public const string TenantId = "22222222-2222-4222-8222-222222222222";
    public const string OrganizationId = "33333333-3333-4333-8333-333333333333";
    public const string DepartmentId = "44444444-4444-4444-8444-444444444444";
    public const string CurrentConfig = "config-2";

    public static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

    public static BulkScoringAccess Access(
        ISharePointSource? source = null,
        Dictionary<string, string?>? settings = null,
        IUserRepository? users = null,
        IBulkRequestAuthorizationRepository? authorizations = null,
        string? assertion = null)
    {
        var values = settings ?? new Dictionary<string, string?> { ["AZURE_TENANT_ID"] = TenantId };
        values.TryAdd("MICROSOFT365_SOURCE_MODE", "mock");
        var options = BulkScoringOptions.Create(key => values.GetValueOrDefault(key), isProduction: false, isDevelopment: true);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(UserId);
        currentUser.SetupGet(u => u.IsAdmin).Returns(true);
        currentUser.Setup(u => u.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentAuthorizationState(
                new CurrentEntraClaims(TenantId, UserId, "actor@example.com", "Actor", "actor@example.com",
                    DateTimeOffset.UtcNow.AddMinutes(-1), new HashSet<string>(), new HashSet<string>()),
                new User { Id = UserId, Username = "actor@example.com", FullName = "Actor", IsActive = true },
                [], [],
                [new RoleAssignment { UserId = UserId, TenantId = TenantId, UserObjectId = UserId, Role = "admin", Source = "delegated", Status = "active" }],
                [], TenantId, null));

        var organizations = new Mock<IOrganizationRepository>();
        organizations.Setup(r => r.GetByIdAsync(OrganizationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization { Id = OrganizationId, Name = "Contoso", Status = "active" });
        organizations.Setup(r => r.GetDepartmentAsync(OrganizationId, DepartmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Department { Id = DepartmentId, OrganizationId = OrganizationId, Name = "HR", Status = "active" });

        return new BulkScoringAccess(
            options, Jobs().Object, currentUser.Object,
            Mock.Of<IUploadSettingsRepository>(), Mock.Of<IBulkImportSettingsRepository>(),
            source ?? Mock.Of<ISharePointSource>(), authorizations ?? Mock.Of<IBulkRequestAuthorizationRepository>(),
            organizations.Object, users);
    }

    public static Mock<IJobRepository> Jobs()
    {
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(j => j.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", Title = "Job", OrganizationId = OrganizationId, DepartmentId = DepartmentId, Status = "active", CurrentConfigVersionId = CurrentConfig });
        return jobs;
    }

    public static BulkScoringRequest Request(BulkRequestStatus status, string id = "req-1", bool raster = false, bool duplicates = false)
    {
        var request = new BulkScoringRequest
        {
            Id = id,
            JobId = "job-1",
            OwnerActorId = UserId,
            SourceKind = BulkSourceKind.SharePoint,
            SourceDriveId = "drive",
            SourceFolderItemId = "folder",
            SourceFolderName = "Candidates",
            SourceFolderPath = "/Candidates",
            SourceWebUrl = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates",
            SourceTenantId = TenantId,
            LimitsJson = BulkRequestLimits.Snapshot(
                BulkScoringOptions.Create(_ => null, false, true), UploadSettings.DefaultMaxIndividualFileBytes, BulkImportSettings.DefaultMaxFilesPerRequest).ToJson(),
            JobConfigVersionId = "config-1",
            DiscoveryStatus = BulkDiscoveryStatus.Complete,
        };
        request.SetOptions(raster, duplicates);
        request.Status = status;
        return request;
    }

    public static BulkScoringItem Item(int ordinal, BulkItemStatus status, BulkReasonCode? reason = null, DateTime? handedOffAt = null, string requestId = "req-1") => new()
    {
        Id = $"item-{ordinal}",
        RequestId = requestId,
        Ordinal = ordinal,
        SourceDriveId = "drive",
        SourceDriveItemId = $"src-{ordinal}",
        SourceETag = $"etag-{ordinal}",
        FileName = $"cv-{ordinal}.pdf",
        SourcePath = $"/Candidates/cv-{ordinal}.pdf",
        MimeType = "application/pdf",
        SizeBytes = 1_000 + ordinal,
        Status = status,
        ReasonCode = reason,
        HandedOffAt = handedOffAt,
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
