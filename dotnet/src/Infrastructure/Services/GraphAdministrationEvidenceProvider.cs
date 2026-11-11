using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Services;

/// <summary>Product application-read evidence only, never source/browser credentials or transitive entitlement.</summary>
public sealed class GraphAdministrationEvidenceProvider(
    HttpClient http, TokenCredential credential, TimeProvider clock,
    string tenantId, string resourceObjectId, string adminAppRoleId) : IDirectoryAdministrationEvidenceProvider
{
    private const string Root = "https://graph.microsoft.com/v1.0/";
    private const string UserSelect = "id,accountEnabled,userType,userPrincipalName,displayName,mail";
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(270);
    private readonly ConcurrentDictionary<string, DirectoryAdministrationEvidence> _cache = new();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Id(string value) => Guid.TryParse(value, out var id) ? id.ToString()
        : throw Error(AuthorizationErrorCodes.GroupEvidenceUnavailable);
    private static DirectoryAdministrationEvidenceException Error(string code = AuthorizationErrorCodes.GroupEvidenceUnavailable) =>
        new(code, AuthorizationErrorCodes.Resolve(code).Message);
    private void Validate(string tenant, string actor, int version)
    {
        if (!Guid.TryParse(tenant, out var tid) || !Guid.TryParse(tenantId, out var configured) || tid != configured
            || !Guid.TryParse(actor, out _) || version < 0) throw Error(AuthorizationErrorCodes.AdminConfigurationInvalid);
    }
    private async Task<string> Token(CancellationToken ct)
    {
        try { return (await credential.GetTokenAsync(new(["https://graph.microsoft.com/.default"]), ct)).Token; }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw Error(); }
    }
    private async Task<JsonElement> Read(string url, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw Error();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error();
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not DirectoryAdministrationEvidenceException && ex is not OperationCanceledException) { throw Error(); }
    }
    private static string? Text(JsonElement value, string field) =>
        value.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool? Bool(JsonElement value, string field) =>
        value.TryGetProperty(field, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : null;
    private async Task<List<JsonElement>> Pages(string path, string token, CancellationToken ct)
    {
        var expected = new Uri(Root + path).AbsolutePath;
        string? next = Root + path;
        var seen = new HashSet<string>();
        var rows = new List<JsonElement>();
        while (next is not null)
        {
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort || uri.AbsolutePath != expected
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !seen.Add(next) || seen.Count > 1000) throw Error();
            var body = await Read(next, token, ct);
            if (!body.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) throw Error();
            foreach (var row in value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw Error();
                rows.Add(row.Clone());
            }
            if (body.TryGetProperty("@odata.nextLink", out var link))
            {
                if (link.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(link.GetString())) throw Error();
                next = link.GetString();
            }
            else next = null;
        }
        return rows;
    }
    private async Task<(bool Enabled, string Type)> User(string actor, string token, CancellationToken ct)
    {
        var user = await Read($"{Root}users/{Id(actor)}?$select={UserSelect}", token, ct);
        if (Text(user, "id") is not { } returned || Id(returned) != Id(actor)
            || Bool(user, "accountEnabled") is not { } enabled || Text(user, "userType") is not { } type
            || type is not ("Member" or "Guest")) throw Error();
        return (enabled, type);
    }
    private static string[] Members(List<JsonElement> rows)
    {
        var ids = rows.Select(row => {
            if (Text(row, "@odata.type") != "#microsoft.graph.user") throw Error(AuthorizationErrorCodes.AdminGroupInvalid);
            return Id(Text(row, "id") ?? "");
        }).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw Error();
        return ids;
    }
    private static bool Same(string[] a, string[] b) => a.Length == b.Length && a.ToHashSet().SetEquals(b);
    private void Fresh(DateTime start) { if (Now >= start + Lifetime) throw Error(); }

    public async Task<DirectoryAdministrationEvidence> GetGroupEvidenceAsync(
        DirectoryAdministrationEvidenceRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request.TenantId, request.ActorObjectId, request.AuthorityVersion);
        var callerCancellation = cancellationToken;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Lifetime);
        cancellationToken = budget.Token;
        var group = Id(request.GroupObjectId);
        var actor = Id(request.ActorObjectId);
        var key = $"{Id(request.TenantId)}:{group}:{request.AuthorityVersion}:{actor}:{request.AuthorizationVersion}";
        if (!request.ForceRefresh && _cache.TryGetValue(key, out var cached) && cached.ExpiresAt > Now) return Copy(cached);
        _cache.TryRemove(key, out _);
        var start = Now;
        try
        {
            var token = await Token(cancellationToken);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var metadata = await Read($"{Root}groups/{group}?$select=id,securityEnabled,groupTypes,visibility", token, cancellationToken);
                if (Id(Text(metadata, "id") ?? "") != group || Bool(metadata, "securityEnabled") != true
                    || !metadata.TryGetProperty("groupTypes", out var types) || types.ValueKind != JsonValueKind.Array
                    || types.EnumerateArray().Any(t => t.GetString() == "Unified")) throw Error(AuthorizationErrorCodes.AdminGroupInvalid);
                if (Text(metadata, "visibility") == "HiddenMembership") throw Error();
                var direct = Members(await Pages($"groups/{group}/members?$select=id", token, cancellationToken));
                var transitive = Members(await Pages($"groups/{group}/transitiveMembers?$select=id", token, cancellationToken));
                if (!Same(direct, transitive)) continue; // Veto; never entitlement.
                var users = new Dictionary<string, (bool Enabled, string Type)>();
                foreach (var member in direct)
                {
                    var user = await User(member, token, cancellationToken);
                    if (user.Type != "Member") throw Error(AuthorizationErrorCodes.AdminGroupInvalid);
                    users[member] = user;
                }
                var eligible = direct.Where(member => users[member].Enabled).ToArray();
                if (eligible.Length == 0) throw Error(AuthorizationErrorCodes.AdminGroupInvalid);
                var actorUser = users.TryGetValue(actor, out var found) ? found : await User(actor, token, cancellationToken);
                if (!Same(direct, Members(await Pages($"groups/{group}/members?$select=id", token, cancellationToken)))) continue;
                var coherent = true;
                foreach (var (member, prior) in users)
                    if (await User(member, token, cancellationToken) != prior) coherent = false;
                if (!coherent) continue;
                Fresh(start);
                var evidence = new DirectoryAdministrationEvidence(Id(request.TenantId), group, request.AuthorityVersion,
                    actor, start, Now, start + Lifetime, true, true, actorUser.Enabled, actorUser.Type, direct, eligible);
                if (_cache.Count >= 256) _cache.Clear();
                _cache[key] = Copy(evidence);
                return evidence;
            }
            throw Error();
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        { _cache.TryRemove(key, out _); throw Error(); }
        catch { _cache.TryRemove(key, out _); throw; }
    }
    private static DirectoryAdministrationEvidence Copy(DirectoryAdministrationEvidence e) =>
        e with { DirectMemberObjectIds = e.DirectMemberObjectIds.ToArray(), EligibleUserObjectIds = e.EligibleUserObjectIds.ToArray() };

    public async Task<DirectoryUserEligibilityEvidence> GetUserEvidenceAsync(
        IndividualDirectoryAdministrationEvidenceRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request.TenantId, request.ObjectId, request.AuthorityVersion);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Lifetime);
        cancellationToken = budget.Token;
        var start = Now;
        var token = await Token(cancellationToken);
        var user = await User(request.ObjectId, token, cancellationToken);
        Fresh(start);
        return new(Id(request.TenantId), Id(request.ObjectId), request.AuthorityVersion, start, Now, start + Lifetime, user.Enabled, user.Type);
    }

    public async Task<IndividualDirectoryAdministrationEvidence> GetIndividualEvidenceAsync(
        IndividualDirectoryAdministrationEvidenceRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request.TenantId, request.ObjectId, request.AuthorityVersion);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Lifetime);
        cancellationToken = budget.Token;
        if (!Guid.TryParse(resourceObjectId, out _) || !Guid.TryParse(adminAppRoleId, out _))
            throw Error(AuthorizationErrorCodes.AdminConfigurationInvalid);
        var start = Now;
        var token = await Token(cancellationToken);
        var actor = Id(request.ObjectId);
        var user = await User(actor, token, cancellationToken);
        if (!user.Enabled) throw Error(AuthorizationErrorCodes.IdentityDisabled);
        if (user.Type != "Member") throw Error(AuthorizationErrorCodes.AssignmentMissing);
        async Task ValidateResource()
        {
            var resource = await Read($"{Root}servicePrincipals/{Id(resourceObjectId)}?$select=id,accountEnabled,appRoles", token, cancellationToken);
            if (Id(Text(resource, "id") ?? "") != Id(resourceObjectId) || Bool(resource, "accountEnabled") != true
                || !resource.TryGetProperty("appRoles", out var roles) || roles.ValueKind != JsonValueKind.Array
                || roles.EnumerateArray().Count(role => Text(role, "id") is { } rid && Id(rid) == Id(adminAppRoleId)
                    && Text(role, "value") == "admin" && Bool(role, "isEnabled") == true
                    && role.TryGetProperty("allowedMemberTypes", out var members) && members.ValueKind == JsonValueKind.Array
                    && members.EnumerateArray().Any(t => t.GetString() == "User")) != 1)
                throw Error(AuthorizationErrorCodes.AdminConfigurationInvalid);
        }
        await ValidateResource();
        var assignments = await Pages($"servicePrincipals/{Id(resourceObjectId)}/appRoleAssignedTo?$select=id,principalId,principalType,resourceId,appRoleId", token, cancellationToken);
        if (assignments.Count(row => Text(row, "principalType") == "User" && Text(row, "principalId") is { } pid && Id(pid) == actor
            && Text(row, "resourceId") is { } rid && Id(rid) == Id(resourceObjectId)
            && Text(row, "appRoleId") is { } aid && Id(aid) == Id(adminAppRoleId)) != 1) throw Error(AuthorizationErrorCodes.AssignmentMissing);
        await ValidateResource();
        user = await User(actor, token, cancellationToken);
        if (!user.Enabled) throw Error(AuthorizationErrorCodes.IdentityDisabled);
        if (user.Type != "Member") throw Error(AuthorizationErrorCodes.AssignmentMissing);
        Fresh(start); // Do not cache positive individual bindings or promise instantaneous Graph propagation.
        return new(Id(request.TenantId), actor, request.AuthorityVersion, start, Now, start + Lifetime, true, "Member", true);
    }
}
