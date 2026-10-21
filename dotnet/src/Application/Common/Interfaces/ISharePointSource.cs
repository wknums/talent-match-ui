using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.Common.Interfaces;

public enum SourcePhase
{
    // A recruiter is on the line using the separate connection-bound authorization-code cache.
    Interactive,
    // The worker is reading after the recruiter has gone: the request's stored authorization is used.
    Background,
}

// Who is reading and which protected delegated authorization may be used. Graph source reads are
// connection-bound interactively and request-bound in the background.
public sealed record SourceCallContext(
    SourcePhase Phase,
    string RecruiterActorId,
    string? RequestId = null,
    string? ConnectionId = null)
{
    public string? AuthorizationBindingId =>
        Phase == SourcePhase.Interactive ? ConnectionId : RequestId;
}

public sealed record SourceFolder(
    BulkSourceKind Kind,
    string DriveId,
    string ItemId,
    string Name,
    string Path,
    string WebUrl,
    string TenantId);

public sealed record SourceFile(
    string DriveId,
    string ItemId,
    string Name,
    string Path,
    long SizeBytes,
    string ETag,
    string? MimeType,
    // package and remoteItem entries are counted as unsupported_type and never imported
    bool IsPackageOrRemote);

// One page of one folder's listing. The cursor is opaque to callers and never leaves the server.
public sealed record SourcePage(
    IReadOnlyList<SourceFile> Files,
    int EntriesScanned,
    int InaccessibleCount,
    string FolderKey,
    // The folder's skip token expired and it was re-listed from its start; the caller reverses the
    // tally contributions it recorded for FolderKey before adding this page.
    bool FolderRestarted,
    string? NextCursor,
    bool Exhausted);

public sealed record SourceItemMetadata(string ETag, long SizeBytes);

public interface ISharePointSource
{
    // Resolves a share link to a folder, verifying the drive type. Throws a Source*Exception on failure.
    Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default);

    Task<SourcePage> ReadNextPageAsync(
        SourceFolder root,
        string? cursor,
        SourceCallContext context,
        CancellationToken cancellationToken = default);

    // Ancestor item identifiers from the item up to the drive root (permitted-roots check).
    [Obsolete("Unbound ancestry reads are not permitted; use exact-root item operations.")]
    Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(
        string driveId,
        string itemId,
        SourceCallContext context,
        CancellationToken cancellationToken = default);

    [Obsolete("Use the exact-root overload.")]
    Task<SourceItemMetadata?> GetItemMetadataAsync(
        string driveId,
        string itemId,
        SourceCallContext context,
        CancellationToken cancellationToken = default);

    // Every item read is bound to the stable drive/folder identity verified through /shares.
    // Implementations must verify item lineage before metadata or content is returned.
    Task<SourceItemMetadata?> GetItemMetadataAsync(
        SourceFolder root,
        string itemId,
        SourceCallContext context,
        CancellationToken cancellationToken = default) =>
        GetItemMetadataAsync(root.DriveId, itemId, context, cancellationToken);

    // Bounded read: throws SourceLimitExceededException when the content is larger than maxBytes.
    [Obsolete("Use the exact-root overload.")]
    Task<byte[]> DownloadAsync(
        string driveId,
        string itemId,
        long maxBytes,
        SourceCallContext context,
        CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(
        SourceFolder root,
        string itemId,
        long maxBytes,
        SourceCallContext context,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(root.DriveId, itemId, maxBytes, context, cancellationToken);

    // Source-side conversion (Graph content?format=pdf) for DOCX and MD.
    [Obsolete("Use the exact-root overload.")]
    Task<byte[]> ConvertToPdfAsync(
        string driveId,
        string itemId,
        long maxBytes,
        SourceCallContext context,
        CancellationToken cancellationToken = default);

    Task<byte[]> ConvertToPdfAsync(
        SourceFolder root,
        string itemId,
        long maxBytes,
        SourceCallContext context,
        CancellationToken cancellationToken = default) =>
        ConvertToPdfAsync(root.DriveId, itemId, maxBytes, context, cancellationToken);

    bool CanConvertToPdf(string extension);
}
