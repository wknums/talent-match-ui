using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.Services;

/// <summary>
/// Default blob store. No-ops on upload, returns sha256 only. Used in
/// sequential mode and as a safe fallback.
/// </summary>
public sealed class InlineBlobStore : IBlobStore
{
    public bool IsRemote => false;

    public Task<BlobUploadResult> PutAsync(
        string jobId, string applicationId, string documentId,
        string fileName, string mimeType, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        var sha = Sha256Hex(bytes);
        return Task.FromResult(new BlobUploadResult(null, sha));
    }

    public Task<byte[]?> ReadByUriAsync(string blobUri, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    internal static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// Azure Blob Storage backed implementation. Uses DefaultAzureCredential —
/// no SAS, ever, per constitution.
/// </summary>
public sealed class AzureBlobStore : IBlobStore
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AzureBlobStore> _logger;

    public bool IsRemote => true;

    public AzureBlobStore(string accountName, string containerName, ILogger<AzureBlobStore> logger)
    {
        var uri = new Uri($"https://{accountName}.blob.core.windows.net");
        var service = new BlobServiceClient(uri, new DefaultAzureCredential());
        _container = service.GetBlobContainerClient(containerName);
        _logger = logger;
    }

    public async Task<BlobUploadResult> PutAsync(
        string jobId, string applicationId, string documentId,
        string fileName, string mimeType, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        var sha = InlineBlobStore.Sha256Hex(bytes);
        var ext = ExtFromName(fileName);
        var blobName = $"{jobId}/{applicationId}/{documentId}.{ext}";
        var blob = _container.GetBlobClient(blobName);

        // Idempotency: skip upload if blob exists with matching sha256 metadata.
        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (props.Value.Metadata.TryGetValue("sha256", out var existingSha) && existingSha == sha)
            {
                return new BlobUploadResult(blob.Uri.ToString(), sha);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Fall through to upload.
        }

        var headers = new BlobHttpHeaders { ContentType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType };
        var metadata = new Dictionary<string, string> { ["sha256"] = sha };

        using (var stream = new MemoryStream(bytes, writable: false))
        {
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = headers,
                Metadata = metadata,
            }, cancellationToken);
        }

        // Sibling .sha256 marker per contract §2.1.
        var marker = _container.GetBlobClient($"{blobName}.sha256");
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(sha)))
        {
            await marker.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain" },
            }, cancellationToken);
        }

        _logger.LogInformation("Uploaded CV {ApplicationId}/{DocumentId} to blob {BlobUri} ({Size} bytes, sha256={Sha})",
            applicationId, documentId, blob.Uri, bytes.Length, sha);

        return new BlobUploadResult(blob.Uri.ToString(), sha);
    }

    public async Task<byte[]?> ReadByUriAsync(string blobUri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobUri)) return null;
        try
        {
            var blob = new BlobClient(new Uri(blobUri), new DefaultAzureCredential());
            var resp = await blob.DownloadContentAsync(cancellationToken);
            return resp.Value.Content.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read blob content from {BlobUri}", blobUri);
            return null;
        }
    }

    private static string ExtFromName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return "bin";
        return name.Substring(dot + 1).ToLowerInvariant();
    }
}
