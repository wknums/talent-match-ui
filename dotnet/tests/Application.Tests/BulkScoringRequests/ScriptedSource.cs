using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

// Replays a fixed list of listing pages. The cursor is the index of the next page, so a step that
// stops and resumes reads exactly where it left off.
internal sealed class ScriptedSource(IReadOnlyList<SourcePage> pages, Action<int>? onRead = null) : ISharePointSource
{
    public int Reads { get; private set; }

    public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default)
    {
        var index = cursor is null ? 0 : int.Parse(cursor);
        Reads++;
        onRead?.Invoke(index);
        var page = pages[index];
        var last = index == pages.Count - 1;
        return Task.FromResult(page with { NextCursor = last ? null : (index + 1).ToString(), Exhausted = last });
    }

    public bool CanConvertToPdf(string extension) => extension is ".docx" or ".md";
    public Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SourceItemMetadata?> GetItemMetadataAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<byte[]> DownloadAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public static SourceFile Pdf(string id, long size = 1_000) => new("d", id, id + ".pdf", "/", size, "e-" + id, null, false);
    public static SourceFile Zip(string id) => new("d", id, id + ".zip", "/", 100, "e-" + id, null, false);

    public static SourcePage Page(IReadOnlyList<SourceFile> files, string folderKey = "root", int inaccessible = 0, int extraEntries = 0, bool restarted = false) =>
        new(files, files.Count + inaccessible + extraEntries, inaccessible, folderKey, restarted, null, false);
}

internal sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * TimestampFrequency);
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => 1_000_000;
}
