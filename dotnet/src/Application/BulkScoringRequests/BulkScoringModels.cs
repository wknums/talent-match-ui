using TalentMatch.Domain.Entities.BulkScoringRequests;
using System.Text.Json.Serialization;

namespace TalentMatch.Application.BulkScoringRequests;

// Wire shapes for contracts/openapi.yaml (camelCase on the wire).
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

public sealed record BulkPreviewFileDto(string ItemId, string FileName, string Path, long SizeBytes, string MimeType, bool ExcludedWhenRasterized);

public sealed record BulkFolderDto(string Name, string Path, string WebUrl);

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

public sealed record SourceConnectionLimitsDto(
    int MaxFilesPerRequest,
    long MaxBytesPerFile,
    long MaxTotalBytes,
    int DiscoveryStepMaxFiles);

public sealed record SourceConnectionPreviewResponse(
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
    string DraftItemsPath,
    BulkFolderDto Folder,
    IReadOnlyList<BulkPreviewFileDto> Included,
    BulkDiscoveryProgressDto Progress,
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
    string? CurrentScoringConfigVersionId,
    bool ConfigChangedSinceAcceptance,
    BulkDiscoveryProgressDto? Discovery,
    string AccessModel,
    string AuthorizationState,
    string SourceTenantId,
    string? CancelledBy = null,
    DateTime? CancelRequestedAt = null)
{
    [JsonIgnore, Obsolete("Use CapturedScoringConfigVersionId.")]
    public string? JobConfigVersionId => CapturedScoringConfigVersionId;
}

// The list and notification shape: everything a progress row needs, without the folder URL, limits or discovery detail.
public sealed record BulkRequestSummaryDto(
    string RequestId,
    string JobId,
    string Status,
    string? Outcome,
    string SourceKind,
    string FolderName,
    string FolderPath,
    DateTime? TerminalAt,
    bool ConvertToRasterPdf,
    bool AllowDuplicates,
    bool CancelRequested,
    string? RetryOfRequestId,
    bool Acknowledged,
    BulkCountsDto Counts,
    int ConcurrencyVersion,
    string AuthorizationState,
    string? CapturedScoringConfigVersionId,
    string? CurrentScoringConfigVersionId,
    bool ConfigChangedSinceAcceptance);

public sealed record BulkRequestPageDto(IReadOnlyList<BulkRequestSummaryDto> Items, int Page, int PageSize, int Total);

public sealed record BulkTerminalCountsDto(
    int Completed,
    int Failed,
    int Skipped,
    int Cancelled);

// A deliberately narrow retained notification. Source/file names and client-provided navigation
// targets are not part of this contract.
public sealed record BulkTerminalNotificationDto(
    string RequestId,
    string RecipientActorId,
    string Outcome,
    BulkTerminalCountsDto Counts,
    DateTime TerminalAt,
    string RequestDetailPath)
{
    // Compatibility for application callers that need to tailor generic authorization-lapse text.
    [JsonIgnore]
    public string AuthorizationState { get; init; } = "not_applicable";
}

// A retained original, always served as a download.
public sealed record BulkOriginalFile(byte[] Content, string FileName);

// Maps to an HTTP status and a stable machine code (contracts/openapi.yaml Error.code).
public sealed class BulkApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    public static BulkApiException NotFound(string message = "The bulk scoring request was not found.") => new(404, "not_found", message);
    public static BulkApiException Forbidden(string message) => new(403, "forbidden", message);
    public static BulkApiException AccessDenied() => new(403, "sharepoint_access_denied", "You do not have access to this folder.");
    public static BulkApiException FolderNotPermitted() => new(403, "sharepoint_folder_not_permitted", "This folder is outside the locations allowed in this environment.");
    public static BulkApiException Invalid(string message, string code = "validation_failed") => new(422, code, message);
    public static BulkApiException UnsupportedSource() => new(422, "unsupported_source", "Only work or school SharePoint and OneDrive folder links are supported.");
    public static BulkApiException Conflict(string code, string message) => new(409, code, message);
    public static BulkApiException DraftExpired() => new(410, "draft_expired", "The preview expired. Run it again.");
    public static BulkApiException AuthorizationRequired() => new(401, "authorization_required", "Sign in again, then repeat this action.");
    public static BulkApiException Unavailable() => new(503, "feature_unavailable", "Bulk scoring from SharePoint or OneDrive is not available in this environment.");
}
