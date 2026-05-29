namespace TalentMatch.Application.Common.Interfaces;

/// <summary>
/// Upload result returned by <see cref="IBlobStore"/>.
/// </summary>
public sealed record BlobUploadResult(string? BlobUri, string Sha256);

/// <summary>
/// Abstraction over CV blob storage. Two implementations exist:
///   - InlineBlobStore (default): no-op upload; returns sha256 only. Used in
///     sequential mode where CVs remain in talentmatch.DocumentBlobs.Content
///     and are handed to the engine over multipart.
///   - AzureBlobStore: uploads CVs to the cv-uploads container via managed
///     identity. Used only when AWR_BLOB_STORAGE_ACCOUNT is set (platform mode).
///
/// Constitution: SAS tokens are forbidden — RBAC via managed identity only.
/// </summary>
public interface IBlobStore
{
    /// <summary>True iff this provider actually writes to Azure Blob Storage.</summary>
    bool IsRemote { get; }

    /// <summary>
    /// Uploads the bytes (if remote) and returns the resulting blob URI plus
    /// content sha256. Idempotent: matching sha256 reuses the existing blob.
    /// </summary>
    Task<BlobUploadResult> PutAsync(
        string jobId,
        string applicationId,
        string documentId,
        string fileName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads blob bytes from an absolute Blob URI. Returns null when the
    /// provider cannot read remote blobs.
    /// </summary>
    Task<byte[]?> ReadByUriAsync(string blobUri, CancellationToken cancellationToken = default);
}
