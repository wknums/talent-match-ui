using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint;

// Registered when MICROSOFT365_SOURCE_MODE is off so the feature's
// handlers still resolve and every operation answers 503 feature_unavailable.
public sealed class DisabledSharePointSource : ISharePointSource
{
    public bool CanConvertToPdf(string extension) => false;
    public Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default) => throw BulkApiException.Unavailable();
    public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default) => throw BulkApiException.Unavailable();
    public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => throw BulkApiException.Unavailable();
    public Task<SourceItemMetadata?> GetItemMetadataAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => throw BulkApiException.Unavailable();
    public Task<byte[]> DownloadAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => throw BulkApiException.Unavailable();
    public Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => throw BulkApiException.Unavailable();
}
