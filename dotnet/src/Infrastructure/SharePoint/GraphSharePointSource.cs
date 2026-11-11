using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Infrastructure.SharePoint;

// Reads SharePoint and OneDrive through Microsoft Graph (research R3-R5). A small typed client over four
// endpoints (/shares, children, item metadata, /content); no SDK. Folder links are never fetched directly:
// they are only turned into a share token for graph.microsoft.com. Bearer tokens are never sent to the
// pre-authenticated download URL Graph redirects to.
public sealed class GraphSharePointSource : ISharePointSource, IDisposable
{
    internal static readonly Uri GraphRoot = new("https://graph.microsoft.com/v1.0/");
    private const int PageSize = 200;
    private const int MaxAncestorDepth = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ISharePointAccessContext _access;
    private readonly SemaphoreSlim _gate;

    // The handler behind `http` must not follow redirects automatically (downloads are redirected by hand).
    public GraphSharePointSource(HttpClient http, ISharePointAccessContext access, BulkScoringOptions options)
    {
        _http = http;
        _access = access;
        _gate = new SemaphoreSlim(Math.Max(1, options.SourceMaxConcurrency));
        ConvertDocxAndMarkdown = true;
    }

    // Spike S1 decides whether source-side conversion of DOCX/MD works under the credential in use.
    public bool ConvertDocxAndMarkdown { get; set; }

    public bool CanConvertToPdf(string extension) =>
        ConvertDocxAndMarkdown && extension.ToLowerInvariant() is ".docx" or ".md" or ".markdown";

    public Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken ct = default) =>
        Throttled(async () =>
        {
            RequireAuthorizationBinding(context);
            var access = await _access.GetGraphAccessAsync(context, ct);
            var share = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(folderUrl))
                .TrimEnd('=').Replace('/', '_').Replace('+', '-');
            using var response = await SendAsync(HttpMethod.Get,
                new Uri(GraphRoot, $"shares/{share}/driveItem?$select=id,name,webUrl,folder,parentReference"),
                context,
                ct,
                access);
            await EnsureSuccessAsync(response, context);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var item = doc.RootElement;
            if (!item.TryGetProperty("folder", out _)) throw new UnsupportedSourceException("The link is not a folder.");

            var parent = item.TryGetProperty("parentReference", out var p) ? p : default;
            var driveId = parent.ValueKind == JsonValueKind.Object ? Str(parent, "driveId") : null;
            var driveType = parent.ValueKind == JsonValueKind.Object ? Str(parent, "driveType") : null;
            if (driveId is null || driveType is null)
            {
                // The drive root has no parent reference: ask for its drive instead.
                using var drive = await SendAsync(
                    HttpMethod.Get,
                    new Uri(GraphRoot, $"shares/{share}/drive?$select=id,driveType"),
                    context,
                    ct,
                    access);
                await EnsureSuccessAsync(drive, context);
                using var driveDoc = JsonDocument.Parse(await drive.Content.ReadAsStringAsync(ct));
                driveId = Str(driveDoc.RootElement, "id");
                driveType = Str(driveDoc.RootElement, "driveType");
            }

            var kind = driveType switch
            {
                "documentLibrary" => BulkSourceKind.SharePoint,
                "business" => BulkSourceKind.OneDriveBusiness,
                _ => throw new UnsupportedSourceException("Only work or school SharePoint and OneDrive are supported."),
            };

            var itemId = Str(item, "id");
            if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(itemId))
                throw new UnsupportedSourceException("Microsoft Graph did not return a stable folder identity.");

            var name = Str(item, "name") ?? "folder";
            var parentPath = parent.ValueKind == JsonValueKind.Object ? Str(parent, "path") : null;
            var relative = parentPath is not null && parentPath.Contains("root:", StringComparison.Ordinal)
                ? parentPath[(parentPath.IndexOf("root:", StringComparison.Ordinal) + 5)..]
                : string.Empty;
            return new SourceFolder(kind, driveId, itemId, name, ("/" + relative.Trim('/') + "/" + name).Replace("//", "/"),
                Str(item, "webUrl") ?? folderUrl, access.SourceTenantId);
        }, ct);

    public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken ct = default) =>
        Throttled(async () =>
        {
            RequireAuthorizationBinding(context);
            var state = cursor is null
                ? new Cursor { Queue = [new QueueEntry(root.ItemId, "/")] }
                : JsonSerializer.Deserialize<Cursor>(cursor, Json)!;

            if (state.Current is null)
            {
                if (state.Queue.Count == 0) return new SourcePage([], 0, 0, root.ItemId, false, null, true);
                state.Current = state.Queue[0];
                state.Queue.RemoveAt(0);
                state.NextLink = null;
            }

            var folder = state.Current;
            var isRoot = folder.Id == root.ItemId;
            var restarted = false;
            var url = state.NextLink is not null
                ? ValidateGraphLink(state.NextLink, root.DriveId, folder.Id)
                : ChildrenUri(root.DriveId, folder.Id);

            HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, context, ct);
            try
            {
                if (state.NextLink is not null && response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.BadRequest)
                {
                    // The skip token expired: list the folder again from its start; the caller reverses earlier tallies.
                    response.Dispose();
                    restarted = true;
                    state.NextLink = null;
                    response = await SendAsync(HttpMethod.Get, ChildrenUri(root.DriveId, folder.Id), context, ct);
                }

                if (!isRoot && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    // A subfolder the caller cannot read is counted, never named.
                    state.Current = null;
                    var done = state.Queue.Count == 0;
                    return new SourcePage([], 0, 1, folder.Id, restarted, done ? null : JsonSerializer.Serialize(state, Json), done);
                }

                await EnsureSuccessAsync(response, context);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var files = new List<SourceFile>();
                var count = 0;
                var inaccessible = 0;
                foreach (var child in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    count++;
                    if (!HasExpectedParent(child, root.DriveId, folder.Id))
                    {
                        // Never trust or disclose an entry whose stable lineage cannot be tied to
                        // the exact folder currently being enumerated.
                        inaccessible++;
                        continue;
                    }

                    var id = Str(child, "id")!;
                    var name = Str(child, "name") ?? id;
                    var isRemote = child.TryGetProperty("remoteItem", out _);
                    if (child.TryGetProperty("folder", out _) && !isRemote)
                    {
                        state.Queue.Add(new QueueEntry(id, (folder.Path.TrimEnd('/') + "/" + name)));
                        continue;
                    }

                    var mime = child.TryGetProperty("file", out var file) ? Str(file, "mimeType") : null;
                    var isPackage = child.TryGetProperty("package", out _);
                    files.Add(new SourceFile(
                        root.DriveId, id, name, folder.Path,
                        child.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                        Str(child, "eTag") ?? string.Empty, mime, isPackage || isRemote || !child.TryGetProperty("file", out _)));
                }

                state.NextLink = Str(doc.RootElement, "@odata.nextLink");
                if (state.NextLink is null) state.Current = null;
                var exhausted = state.Current is null && state.Queue.Count == 0;
                return new SourcePage(files, count, inaccessible, folder.Id, restarted,
                    exhausted ? null : JsonSerializer.Serialize(state, Json), exhausted);
            }
            finally
            {
                response.Dispose();
            }
        }, ct);

    public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken ct = default) =>
        throw UnboundRead();

    public Task<SourceItemMetadata?> GetItemMetadataAsync(string driveId, string itemId, SourceCallContext context, CancellationToken ct = default) =>
        throw UnboundRead();

    public Task<SourceItemMetadata?> GetItemMetadataAsync(
        SourceFolder root,
        string itemId,
        SourceCallContext context,
        CancellationToken ct = default) =>
        Throttled(async () =>
        {
            RequireAuthorizationBinding(context);
            var item = await VerifyLineageAsync(root, itemId, context, missingIsNull: true, ct);
            return item is null
                ? null
                : new SourceItemMetadata(item.Value.ETag, item.Value.SizeBytes);
        }, ct);

    public Task<byte[]> DownloadAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken ct = default) =>
        throw UnboundRead();

    public Task<byte[]> DownloadAsync(
        SourceFolder root,
        string itemId,
        long maxBytes,
        SourceCallContext context,
        CancellationToken ct = default) =>
        Throttled(async () =>
        {
            RequireAuthorizationBinding(context);
            _ = await VerifyLineageAsync(root, itemId, context, missingIsNull: false, ct);
            return await ReadContentAsync(ItemUri(root.DriveId, itemId, "/content"), maxBytes, context, ct);
        }, ct);

    public Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken ct = default) =>
        throw UnboundRead();

    public Task<byte[]> ConvertToPdfAsync(
        SourceFolder root,
        string itemId,
        long maxBytes,
        SourceCallContext context,
        CancellationToken ct = default) =>
        Throttled(async () =>
        {
            RequireAuthorizationBinding(context);
            _ = await VerifyLineageAsync(root, itemId, context, missingIsNull: false, ct);
            return await ReadContentAsync(
                ItemUri(root.DriveId, itemId, "/content?format=pdf"), maxBytes, context, ct);
        }, ct);

    public void Dispose() => _gate.Dispose();

    private async Task<byte[]> ReadContentAsync(Uri uri, long maxBytes, SourceCallContext context, CancellationToken ct)
    {
        using var first = await SendAsync(HttpMethod.Get, uri, context, ct);
        var response = first;
        HttpResponseMessage? followed = null;
        try
        {
            if (first.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.RedirectKeepVerb or HttpStatusCode.MovedPermanently
                or HttpStatusCode.SeeOther or HttpStatusCode.PermanentRedirect)
            {
                var location = first.Headers.Location ?? throw new SourceTransientException("Graph redirected without a location.");
                if (!location.IsAbsoluteUri) location = new Uri(uri, location);
                if (location.Scheme != Uri.UriSchemeHttps) throw new SourceTransientException("Graph redirected to a non-HTTPS address.");
                // The download address is pre-authenticated: the bearer token must not accompany it.
                using var request = new HttpRequestMessage(HttpMethod.Get, location);
                followed = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response = followed;
            }

            await EnsureSuccessAsync(response, context);
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                throw new SourceLimitExceededException("The file is larger than the allowed size.", maxBytes);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new SourceLimitExceededException("The file is larger than the allowed size.", maxBytes);
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        finally
        {
            followed?.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        SourceCallContext context,
        CancellationToken ct,
        DelegatedGraphAccess? access = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            (access ?? await _access.GetGraphAccessAsync(context, ct)).AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceTransientException("Microsoft Graph could not be reached.", inner: ex);
        }
    }

    // Maps Graph failures onto the classes the worker retries or fails on (research R9).
    private Task EnsureSuccessAsync(HttpResponseMessage response, SourceCallContext context)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized when context.Phase == SourcePhase.Background:
                throw new SourceAuthorizationLapsedException("graph_unauthorized");
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new SourceAccessDeniedException("Access to the item was denied.");
            case HttpStatusCode.NotFound:
                throw new SourceNotFoundException("The item was not found.");
            case HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError:
                throw new SourceTransientException($"Microsoft Graph answered {(int)response.StatusCode}.", RetryAfter(response));
            default:
                throw new UnsupportedSourceException($"Microsoft Graph rejected the request ({(int)response.StatusCode}).");
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow is { Ticks: > 0 } wait ? wait : TimeSpan.Zero;
        return null;
    }

    private async Task<T> Throttled<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await operation(); }
        finally { _gate.Release(); }
    }

    private static Uri ChildrenUri(string driveId, string itemId) => new(GraphRoot,
        $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/children"
        + $"?$select=id,name,size,eTag,file,folder,package,remoteItem,parentReference&$top={PageSize}");

    private static Uri ItemUri(string driveId, string itemId, string suffix) => new(GraphRoot,
        $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}{suffix}");

    // A paging link must stay on Graph: otherwise a hostile response could make us send our token elsewhere.
    private static Uri ValidateGraphLink(string link, string driveId, string folderItemId)
    {
        var expectedPath = ChildrenUri(driveId, folderItemId).AbsolutePath;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, GraphRoot.Host, StringComparison.OrdinalIgnoreCase))
            throw new SourceTransientException("Microsoft Graph returned an unexpected paging link.");
        if (!string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
            throw new SourceTransientException("Microsoft Graph returned an unexpected paging link.");
        return uri;
    }

    private async Task<VerifiedItem?> VerifyLineageAsync(
        SourceFolder root,
        string itemId,
        SourceCallContext context,
        bool missingIsNull,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root.DriveId)
            || string.IsNullOrWhiteSpace(root.ItemId)
            || string.IsNullOrWhiteSpace(itemId))
            throw UnboundRead();

        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = itemId;
        VerifiedItem? requested = null;

        while (current is not null && visited.Count < MaxAncestorDepth)
        {
            if (requested is not null && string.Equals(current, root.ItemId, StringComparison.Ordinal))
                return requested;
            if (!visited.Add(current)) throw UnboundRead();
            using var response = await SendAsync(HttpMethod.Get,
                ItemUri(root.DriveId, current, "?$select=id,eTag,size,parentReference"), context, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && requested is null && missingIsNull)
                return null;
            await EnsureSuccessAsync(response, context);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var item = doc.RootElement;
            if (!string.Equals(Str(item, "id"), current, StringComparison.Ordinal))
                throw UnboundRead();

            if (requested is null)
                requested = new VerifiedItem(
                    Str(item, "eTag") ?? string.Empty,
                    item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0);

            if (string.Equals(current, root.ItemId, StringComparison.Ordinal))
                return requested;

            if (!item.TryGetProperty("parentReference", out var parent)
                || parent.ValueKind != JsonValueKind.Object
                || !string.Equals(Str(parent, "driveId"), root.DriveId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(Str(parent, "id")))
                throw UnboundRead();

            current = Str(parent, "id");
        }

        throw UnboundRead();
    }

    private static bool HasExpectedParent(JsonElement item, string driveId, string folderItemId) =>
        !string.IsNullOrWhiteSpace(Str(item, "id"))
        && item.TryGetProperty("parentReference", out var parent)
        && parent.ValueKind == JsonValueKind.Object
        && string.Equals(Str(parent, "driveId"), driveId, StringComparison.Ordinal)
        && string.Equals(Str(parent, "id"), folderItemId, StringComparison.Ordinal);

    private static void RequireAuthorizationBinding(SourceCallContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.AuthorizationBindingId)) return;
        if (context.Phase == SourcePhase.Background)
            throw new SourceAuthorizationLapsedException("request_authorization_missing");
        throw new AuthorizationRequiredException(
            "Complete the Microsoft 365 source connection before reading folder content.");
    }

    private static SourceAccessDeniedException UnboundRead() =>
        new("The item is not within the verified source folder.");

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record QueueEntry(string Id, string Path);
    private readonly record struct VerifiedItem(string ETag, long SizeBytes);

    private sealed class Cursor
    {
        public List<QueueEntry> Queue { get; set; } = [];
        public QueueEntry? Current { get; set; }
        public string? NextLink { get; set; }
    }
}
