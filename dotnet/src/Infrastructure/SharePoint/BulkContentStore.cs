using System.Security.Cryptography;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Infrastructure.SharePoint;

// Staged and retained bytes follow the existing one-location rule: Azure Blob (managed identity, no SAS)
// when AWR_BLOB_STORAGE_ACCOUNT is configured, otherwise inline in the database. Objects live under
// bulk/{requestId}/{itemId}/{kind} so a request's content can be deleted by prefix with its job (FR-021).
public sealed class BulkContentStore(IBlobStore blobStore) : IBulkContentStore
{
    public async Task<BulkScoringItemContent> StageAsync(
        string requestId,
        string itemId,
        BulkContentKind kind,
        string fileName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId) || itemId.Contains('/'))
            throw new ArgumentException("An item identifier without path separators is required.", nameof(itemId));
        if (bytes.Length == 0)
            throw new BulkConversionException(
                kind == BulkContentKind.Converted ? BulkReasonCode.ConvertedDocumentInvalid : BulkReasonCode.EmptyFile,
                "Empty content cannot be staged.");
        if (kind == BulkContentKind.Converted)
        {
            if (!string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                throw new BulkConversionException(BulkReasonCode.ConvertedTypeInvalid, "Converted content must be a PDF.");
            if (!bytes.AsSpan().StartsWith("%PDF-"u8))
                throw new BulkConversionException(BulkReasonCode.ConvertedMagicInvalid, "Converted content does not have a valid PDF signature.");
        }

        var blobName = $"{Prefix(requestId)}{itemId}/{kind.ToWire()}";
        var result = await blobStore.PutObjectAsync(blobName, mimeType, bytes, cancellationToken);

        var content = new BulkScoringItemContent
        {
            ItemId = itemId,
            Kind = kind,
            SizeBytes = bytes.LongLength,
            Sha256 = result.Sha256,
        };
        if (blobStore.IsRemote && result.BlobUri is not null) content.BlobUri = RequireCredentialFreeStorageUri(result.BlobUri);
        else content.ContentBase64 = Convert.ToBase64String(bytes);
        content.ValidateInvariants();
        return content;
    }

    public async Task<byte[]> ReadAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default)
    {
        content.ValidateInvariants();
        var bytes = content.ContentBase64 is not null
            ? Convert.FromBase64String(content.ContentBase64)
            : await blobStore.ReadByUriAsync(content.BlobUri!, cancellationToken)
                ?? throw new InvalidOperationException($"The staged {content.Kind.ToWire()} content could not be read.");
        if (bytes.LongLength != content.SizeBytes
            || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), content.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The staged {content.Kind.ToWire()} content failed integrity validation.");
        return bytes;
    }

    public Task DeleteAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default) =>
        content.BlobUri is null ? Task.CompletedTask : blobStore.DeleteByUriAsync(content.BlobUri, cancellationToken);

    public Task DeleteByRequestAsync(string requestId, CancellationToken cancellationToken = default) =>
        blobStore.DeleteByPrefixAsync(Prefix(requestId), cancellationToken);

    private static string Prefix(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Contains('/'))
            throw new ArgumentException("A request identifier without path separators is required.", nameof(requestId));
        return $"bulk/{requestId}/";
    }

    private static string RequireCredentialFreeStorageUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Bulk content storage must use a credential-free HTTPS location.");
        return uri.AbsoluteUri;
    }
}
