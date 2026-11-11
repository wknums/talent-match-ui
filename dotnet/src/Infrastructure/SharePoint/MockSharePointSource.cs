using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Infrastructure.SharePoint;

// Deterministic in-repo source over tests/fixtures/bulk-scoring-requests/graph-tree.json, used when
// MICROSOFT365_SOURCE_MODE=mock. It models SharePoint and OneDrive drives, per-user permissions (an item the
// recruiter cannot open is never listed, named, or read), resumable listing with expiring skip tokens,
// and eTag changes, so the whole workflow runs without a tenant.
public sealed class MockSharePointSource : ISharePointSource
{
    private const int PageSize = 200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ISharePointAccessContext _access;
    private readonly string _fixtureRoot;
    private readonly TreeModel _tree;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    private readonly HashSet<string> _principals;
    private readonly ConcurrentDictionary<string, int> _versions = new();
    private readonly ConcurrentDictionary<string, bool> _deleted = new();
    private readonly ConcurrentDictionary<string, bool> _expireSkipToken = new();
    private readonly ConcurrentDictionary<string, string> _actorPrincipals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Principal, string ItemId), bool> _revoked = new();

    public MockSharePointSource(ISharePointAccessContext access, string? fixtureRoot = null)
    {
        _access = access;
        _fixtureRoot = fixtureRoot ?? MockFixtureLocator.Find();
        _tree = JsonSerializer.Deserialize<TreeModel>(File.ReadAllText(Path.Combine(_fixtureRoot, "graph-tree.json")), Json)
            ?? throw new InvalidOperationException("graph-tree.json is empty.");
        Build();
        _principals = new HashSet<string>(
            _nodes.Values.SelectMany(n => n.Acl ?? []).Where(p => p != "*").Append(_tree.DefaultConnectedAccount),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool ConvertDocxAndMarkdown { get; set; } = true;

    // Test and demo hooks.
    public void Revoke(string principal, string itemId) => _revoked[(principal, itemId)] = true;
    public void Restore(string principal, string itemId) => _revoked.TryRemove((principal, itemId), out _);
    public void ChangeItem(string itemId) => _versions.AddOrUpdate(itemId, 1, (_, v) => v + 1);
    public void DeleteItem(string itemId) => _deleted[itemId] = true;
    public void ExpireSkipTokenOnce(string folderItemId) => _expireSkipToken[folderItemId] = true;
    public void MapActor(string actorId, string principal) => _actorPrincipals[actorId] = principal;

    public bool CanConvertToPdf(string extension) =>
        ConvertDocxAndMarkdown && extension.ToLowerInvariant() is ".docx" or ".md" or ".markdown";

    public async Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default)
    {
        await _access.GetGraphAccessAsync(context, cancellationToken);
        if (!Uri.TryCreate(folderUrl, UriKind.Absolute, out var uri))
            throw new UnsupportedSourceException("The link is not a valid address.");

        var drive = _tree.Drives
            .Select(d => (Drive: d, Root: new Uri(d.WebRoot)))
            .Where(x => string.Equals(x.Root.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
                && Uri.UnescapeDataString(uri.AbsolutePath).StartsWith(Uri.UnescapeDataString(x.Root.AbsolutePath), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Root.AbsolutePath.Length)
            .Select(x => ((DriveModel Drive, Uri Root)?)x)
            .FirstOrDefault()
            ?? throw new SourceNotFoundException("The folder could not be found.");

        var rootPath = Uri.UnescapeDataString(drive.Root.AbsolutePath).TrimEnd('/');
        var remainder = Uri.UnescapeDataString(uri.AbsolutePath)[rootPath.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        var node = _nodes[drive.Drive.RootItemId];
        foreach (var segment in remainder)
        {
            node = ChildrenOf(node.Id)
                .FirstOrDefault(c => c.IsFolder && string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase))
                ?? throw new SourceNotFoundException("The folder could not be found.");
        }

        if (!node.IsFolder) throw new UnsupportedSourceException("The link is not a folder.");
        if (!CanOpen(node, Principal(context)))
            throw new SourceAccessDeniedException("You do not have access to this folder.");

        return new SourceFolder(
            WireEnum.Parse<BulkSourceKind>(drive.Drive.Kind),
            drive.Drive.Id,
            node.Id,
            node.Name,
            "/" + string.Join('/', remainder),
            folderUrl,
            drive.Drive.TenantId);
    }

    public async Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default)
    {
        await _access.GetGraphAccessAsync(context, cancellationToken);
        var principal = Principal(context);
        var state = cursor is null
            ? new Cursor { Queue = [root.ItemId] }
            : JsonSerializer.Deserialize<Cursor>(cursor, Json)!;

        if (state.Current is null)
        {
            if (state.Queue.Count == 0)
                return new SourcePage([], 0, 0, root.ItemId, false, null, true);
            state.Current = state.Queue[0];
            state.Queue.RemoveAt(0);
            state.Offset = 0;
        }

        var folderId = state.Current;
        var restarted = false;
        if (state.Offset > 0 && _expireSkipToken.TryRemove(folderId, out _))
        {
            restarted = true;
            state.Offset = 0;
        }

        var children = ChildrenOf(folderId);
        var slice = children.Skip(state.Offset).Take(PageSize).ToList();
        var files = new List<SourceFile>();
        var inaccessible = 0;
        foreach (var child in slice)
        {
            if (!CanOpen(child, principal)) { inaccessible++; continue; }
            if (child.IsFolder) { state.Queue.Add(child.Id); continue; }
            files.Add(new SourceFile(
                child.DriveId, child.Id, child.Name, RelativePath(child, root.ItemId), SizeOf(child), ETag(child), MimeOf(child.Name), false));
        }

        state.Offset += slice.Count;
        if (state.Offset >= children.Count) state.Current = null;
        var exhausted = state.Current is null && state.Queue.Count == 0;
        return new SourcePage(
            files, slice.Count, inaccessible, folderId, restarted,
            exhausted ? null : JsonSerializer.Serialize(state, Json), exhausted);
    }

    public async Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default)
    {
        await _access.GetGraphAccessAsync(context, cancellationToken);
        var chain = new List<string>();
        var node = Find(itemId);
        while (node is not null)
        {
            chain.Add(node.Id);
            node = node.ParentId is null ? null : Find(node.ParentId);
        }
        return chain;
    }

    public async Task<SourceItemMetadata?> GetItemMetadataAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default)
    {
        await _access.GetGraphAccessAsync(context, cancellationToken);
        var node = Find(itemId);
        if (node is null || _deleted.ContainsKey(itemId)) return null;
        if (!CanOpen(node, Principal(context))) throw new SourceAccessDeniedException("Access to the file was denied.");
        return new SourceItemMetadata(ETag(node), SizeOf(node));
    }

    public async Task<byte[]> DownloadAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default)
    {
        var node = await Readable(itemId, context, cancellationToken);
        if (SizeOf(node) > maxBytes)
            throw new SourceLimitExceededException("The file is larger than the allowed size.", maxBytes);
        return ContentOf(node);
    }

    public async Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default)
    {
        var node = await Readable(itemId, context, cancellationToken);
        if (!CanConvertToPdf(Path.GetExtension(node.Name)))
            throw new UnsupportedSourceException("The source cannot convert this file type to PDF.");
        return File.ReadAllBytes(Path.Combine(_fixtureRoot, "files", "pdf-1page.pdf"));
    }

    private async Task<Node> Readable(string itemId, SourceCallContext context, CancellationToken cancellationToken)
    {
        await _access.GetGraphAccessAsync(context, cancellationToken);
        var node = Find(itemId);
        if (node is null || node.IsFolder || _deleted.ContainsKey(itemId))
            throw new SourceNotFoundException("The file no longer exists.");
        if (!CanOpen(node, Principal(context)))
            throw new SourceAccessDeniedException("Access to the file was denied.");
        return node;
    }

    private string Principal(SourceCallContext context)
    {
        if (_actorPrincipals.TryGetValue(context.RecruiterActorId, out var mapped)) return mapped;
        return _principals.Contains(context.RecruiterActorId) ? context.RecruiterActorId : _tree.DefaultConnectedAccount;
    }

    private bool CanOpen(Node node, string principal)
    {
        var aclDecided = false;
        var allowed = true;
        for (var current = node; current is not null; current = current.ParentId is null ? null : Find(current.ParentId))
        {
            if (_revoked.ContainsKey((principal, current.Id))) return false;
            if (!aclDecided && current.Acl is not null)
            {
                aclDecided = true;
                allowed = current.Acl.Contains("*") || current.Acl.Contains(principal, StringComparer.OrdinalIgnoreCase);
            }
        }
        return allowed;
    }

    private string ETag(Node node) => $"etag-{node.Id}-{_versions.GetValueOrDefault(node.Id)}";

    private long SizeOf(Node node)
    {
        if (node.DeclaredSize is { } declared) return declared;
        if (node.Generated is not null) return node.Generated == 'e' ? 1024 : 512;
        return new FileInfo(Path.Combine(_fixtureRoot, "files", node.FixtureFile!)).Length;
    }

    private byte[] ContentOf(Node node) => node.Generated is null
        ? File.ReadAllBytes(Path.Combine(_fixtureRoot, "files", node.FixtureFile!))
        : Encoding.UTF8.GetBytes($"%PDF-1.4\n% synthetic candidate {node.Id}\n%%EOF\n");

    private string RelativePath(Node file, string rootItemId)
    {
        var parts = new List<string>();
        var current = file.ParentId is null ? null : Find(file.ParentId);
        while (current is not null && current.Id != rootItemId)
        {
            parts.Insert(0, current.Name);
            current = current.ParentId is null ? null : Find(current.ParentId);
        }
        return "/" + string.Join('/', parts);
    }

    private static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    private Node? Find(string id)
    {
        if (_nodes.TryGetValue(id, out var node)) return node;
        // gen:{subfolderIndex}:{n}:{e|x}, created on demand so the Large tree costs no memory up front
        var parts = id.Split(':');
        if (parts.Length == 4 && parts[0] == "gen" && _nodes.TryGetValue($"gen-folder:{parts[1]}", out var parent))
        {
            var kind = parts[3][0];
            return new Node(id, parent.DriveId, parent.Id, kind == 'e' ? $"cv-{parts[1]}-{parts[2]}.pdf" : $"junk-{parts[1]}-{parts[2]}.bin", false, null, null, null, kind);
        }
        return null;
    }

    private List<Node> ChildrenOf(string folderId)
    {
        if (_children.TryGetValue(folderId, out var ids)) return ids.Select(i => Find(i)!).ToList();
        if (folderId.StartsWith("gen-folder:", StringComparison.Ordinal) && int.TryParse(folderId["gen-folder:".Length..], out var sub))
        {
            var spec = _tree.Generated[0];
            var junk = spec.NonCandidate / spec.Subfolders;
            var eligible = spec.Eligible / spec.Subfolders;
            var list = new List<Node>(junk + eligible);
            for (var n = 0; n < junk; n++) list.Add(Find($"gen:{sub:00}:{n:0000}:x")!);
            for (var n = 0; n < eligible; n++) list.Add(Find($"gen:{sub:00}:{n:0000}:e")!);
            return list;
        }
        return [];
    }

    private void Build()
    {
        foreach (var item in _tree.Items)
            _nodes[item.Id] = new Node(item.Id, item.DriveId, item.ParentId, item.Name, item.Folder, item.File, item.DeclaredSize, item.Acl, null);

        foreach (var generated in _tree.Generated)
        {
            _nodes[generated.FolderItemId] = new Node(generated.FolderItemId, generated.DriveId, generated.ParentId, generated.Name, true, null, null, null, null);
            for (var sub = 0; sub < generated.Subfolders; sub++)
            {
                var id = $"gen-folder:{sub:00}";
                _nodes[id] = new Node(id, generated.DriveId, generated.FolderItemId, $"sub-{sub:00}", true, null, null, null, null);
            }
        }

        foreach (var group in _nodes.Values.Where(n => n.ParentId is not null).GroupBy(n => n.ParentId!))
            _children[group.Key] = group.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => n.Id).ToList();
    }

    private sealed record Node(string Id, string DriveId, string? ParentId, string Name, bool IsFolder, string? FixtureFile, long? DeclaredSize, string[]? Acl, char? Generated);

    private sealed class Cursor
    {
        public List<string> Queue { get; set; } = [];
        public string? Current { get; set; }
        public int Offset { get; set; }
    }

    private sealed class TreeModel
    {
        public string DefaultConnectedAccount { get; set; } = string.Empty;
        public List<DriveModel> Drives { get; set; } = [];
        public List<ItemModel> Items { get; set; } = [];
        public List<GeneratedModel> Generated { get; set; } = [];
    }

    private sealed class DriveModel
    {
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string DriveType { get; set; } = string.Empty;
        public string WebRoot { get; set; } = string.Empty;
        public string RootItemId { get; set; } = string.Empty;
    }

    private sealed class ItemModel
    {
        public string Id { get; set; } = string.Empty;
        public string DriveId { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Folder { get; set; }
        public string? File { get; set; }
        public long? DeclaredSize { get; set; }
        public string[]? Acl { get; set; }
    }

    private sealed class GeneratedModel
    {
        public string FolderItemId { get; set; } = string.Empty;
        public string DriveId { get; set; } = string.Empty;
        public string ParentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Eligible { get; set; }
        public int NonCandidate { get; set; }
        public int Subfolders { get; set; }
    }
}

internal static class MockFixtureLocator
{
    public static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "bulk-scoring-requests");
                if (File.Exists(Path.Combine(candidate, "graph-tree.json"))) return candidate;
            }
        }
        throw new DirectoryNotFoundException("tests/fixtures/bulk-scoring-requests/graph-tree.json was not found; run the mock source from a repository checkout.");
    }
}
