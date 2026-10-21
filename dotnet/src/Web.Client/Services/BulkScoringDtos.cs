using System.Text.Json.Serialization;

namespace TalentMatch.Web.Client.Services;

// Wire shapes for specs/001-sharepoint-bulk-scoring/contracts/openapi.yaml (camelCase JSON).

public sealed record BulkLimitsDto(int MaxFilesPerRequest, long MaxBytesPerFile, long MaxTotalBytes, int MaxPagesPerDocument, int DiscoveryStepFiles);

public sealed record BulkCapabilitiesDto(
    bool Enabled,
    string Mode,
    string AccessModel,
    IReadOnlyList<string> SupportedSources,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<string> ConvertibleExtensions,
    BulkLimitsDto Limits);

public sealed record BulkSettingsDto(int MaxFilesPerRequest, int ConcurrencyVersion, DateTime? UpdatedAt, string? UpdatedBy);

public sealed record UpdateBulkSettingsRequest(int MaxFilesPerRequest, int ExpectedConcurrencyVersion);

public sealed record BulkPreviewRequest(string FolderUrl);

public sealed record BulkConfirmRequest(bool ConvertToRasterPdf, bool AllowDuplicates);

public sealed record BeginSourceConnectionRequest(string FolderUrl, string? RetryOfRequestId = null);

public sealed record BeginSourceConnectionResponseDto(
    string ConnectionId,
    string Status,
    string AuthorizationUrl,
    DateTimeOffset ExpiresAt);

public sealed record SourceConnectionStatusDto(
    string ConnectionId,
    string Status,
    DateTimeOffset ExpiresAt,
    string? SourceKind = null,
    string? SourceAccountDisplay = null,
    string? FolderName = null,
    bool AdminConsentRequired = false,
    string? AdminConsentUrl = null,
    string? SafeFailureCode = null);

public sealed record SourceConnectionLimitsDto(
    int MaxFilesPerRequest,
    long MaxBytesPerFile,
    long MaxTotalBytes,
    int DiscoveryStepMaxFiles);

public sealed record SourceConnectionPreviewDto(
    string RequestId,
    string ConnectionId,
    string SourceKind,
    string SourceAccountDisplay,
    string FolderName,
    string AuthorizationState,
    string DiscoveryStatus,
    int ReviewSnapshotVersion,
    int ConcurrencyVersion,
    BulkCountsDto Counts,
    IReadOnlyDictionary<string, int> ExcludedByReason,
    int EntriesScanned,
    long TotalEligibleBytes,
    SourceConnectionLimitsDto Limits,
    string DraftItemsPath);

public sealed record BulkDraftItemDto(
    string ItemId,
    string FileName,
    string RelativePath,
    long SizeBytes,
    string MimeType);

public sealed record BulkDraftItemPageDto(
    IReadOnlyList<BulkDraftItemDto> Items,
    int TotalItems,
    int ReviewSnapshotVersion,
    string? NextReviewCursor);

public sealed record BulkConfirmWithVersionRequest(
    bool ConvertToRasterPdf,
    bool AllowDuplicates,
    int ExpectedConcurrencyVersion);

public sealed record BulkFolderDto(string Name, string Path, string WebUrl);

public sealed record BulkPreviewFileDto(string ItemId, string FileName, string Path, long SizeBytes, string MimeType, bool ExcludedWhenRasterized);

public sealed record BulkDiscoveryProgressDto(
    string DiscoveryStatus,
    int EntriesScanned,
    int StepCount,
    int IncludedCount,
    long IncludedBytes,
    int IncludedCountIfRasterized,
    long IncludedBytesIfRasterized,
    int ExcludedCount,
    IReadOnlyDictionary<string, int> ExcludedByReason,
    int InaccessibleItemCount);

public sealed record BulkPreviewResponseDto(
    string RequestId,
    BulkFolderDto Folder,
    IReadOnlyList<BulkPreviewFileDto> Included,
    BulkDiscoveryProgressDto Progress,
    BulkLimitsDto Limits,
    DateTime ExpiresAt);

public sealed record BulkDiscoveryStepDto(string RequestId, BulkDiscoveryProgressDto Progress, DateTime ExpiresAt);

public sealed record BulkCountsDto(
    int Discovered, int Excluded, int Unreadable, int Waiting, int Importing, int Converting,
    int Scoring, int Completed, int Failed, int Skipped, int Cancelled);

public sealed record BulkRequestDetailDto(
    string RequestId,
    string JobId,
    string Status,
    string? Outcome,
    string SourceKind,
    string FolderName,
    string FolderPath,
    string FolderWebUrl,
    string SubmittedBy,
    DateTime SubmittedAt,
    DateTime? TerminalAt,
    bool ConvertToRasterPdf,
    bool AllowDuplicates,
    bool CancelRequested,
    string? RetryOfRequestId,
    bool Acknowledged,
    BulkCountsDto Counts,
    int ConcurrencyVersion,
    BulkLimitsDto Limits,
    string? CapturedScoringConfigVersionId,
    bool ConfigChangedSinceAcceptance,
    BulkDiscoveryProgressDto? Discovery,
    string AccessModel,
    string AuthorizationState,
    string SourceTenantId,
    string? CancelledBy = null,
    string? CurrentScoringConfigVersionId = null,
    DateTime? CancelRequestedAt = null)
{
    [JsonIgnore]
    public string? JobConfigVersionId => CapturedScoringConfigVersionId;
}

// The list and notification shape.
public sealed record BulkRequestSummaryDto(
    string RequestId,
    string JobId,
    string Status,
    string? Outcome,
    string SourceKind,
    string FolderName,
    string FolderPath,
    string SubmittedBy,
    DateTime SubmittedAt,
    DateTime? TerminalAt,
    bool ConvertToRasterPdf,
    bool AllowDuplicates,
    bool CancelRequested,
    string? RetryOfRequestId,
    bool Acknowledged,
    BulkCountsDto Counts,
    int ConcurrencyVersion,
    string AuthorizationState,
    string? CapturedScoringConfigVersionId = null,
    string? CurrentScoringConfigVersionId = null,
    bool ConfigChangedSinceAcceptance = false,
    [property: JsonIgnore] string? RecipientActorId = null,
    [property: JsonIgnore] string? RequestDetailPath = null);

public sealed record BulkRequestPageDto(IReadOnlyList<BulkRequestSummaryDto> Items, int Page, int PageSize, int Total);

public sealed record CancelBulkRequest(int ExpectedConcurrencyVersion);

public sealed record BulkTerminalCountsDto(int Completed, int Failed, int Skipped, int Cancelled);

// Deliberately excludes source and item names. Navigation is a server-generated relative API path.
public sealed record BulkTerminalNotificationDto(
    string RequestId,
    string RecipientActorId,
    string Outcome,
    BulkTerminalCountsDto Counts,
    DateTime TerminalAt,
    string RequestDetailPath);

public sealed record BulkItemDto(
    string ItemId, string FileName, string Path, long SizeBytes, string Status, string? ReasonCode, string? ReasonMessage,
    int Attempt, string? ApplicationId, string? ScoredVersion, bool HasRetainedOriginal, bool ExcludedWhenRasterized,
    string MimeType, int? PageCount, long? ConvertedSizeBytes, DateTime UpdatedAt, string? ScoringConfigVersionId = null);

public sealed record BulkItemPageDto(IReadOnlyList<BulkItemDto> Items, int Page, int PageSize, int Total);
