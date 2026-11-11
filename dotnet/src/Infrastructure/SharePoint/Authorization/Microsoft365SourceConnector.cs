using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using TalentMatch.Application.BulkScoringRequests;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

public interface ISourceAuthorizationStateStore
{
    Task StoreAsync(SourceAuthorizationState state, CancellationToken cancellationToken = default);
    Task<SourceAuthorizationState?> TakeAsync(string stateHash, CancellationToken cancellationToken = default);
}

public enum SourceAuthorizationStatePurpose
{
    UserAuthorization,
    AdminConsent,
}

public sealed record SourceAuthorizationRequest(
    string ConnectionId,
    string ProductActorId,
    string JobId,
    string CanonicalUrlHash,
    string RedirectAfterPath,
    TimeSpan StateLifetime);

public sealed record SourceAuthorizationState(
    string StateHash,
    string CodeVerifier,
    SourceAuthorizationStatePurpose Purpose,
    string ConnectionId,
    string ProductActorId,
    string JobId,
    string CanonicalUrlHash,
    string RedirectAfterPath,
    DateTimeOffset ExpiresAt);

public sealed record SourceAuthorizationStart(
    string ConnectionId,
    string AuthorizationUrl,
    DateTimeOffset ExpiresAt);

public static class SourceAuthorizationCryptography
{
    public static string HashState(string state) => Hash(Encoding.UTF8.GetBytes(state));

    public static string CreateS256Challenge(string codeVerifier) =>
        Hash(Encoding.ASCII.GetBytes(codeVerifier));

    public static string HashAccount(string tenantId, string accountId) =>
        Hash(Encoding.UTF8.GetBytes($"{tenantId}\n{accountId}"));

    internal static string RandomOpaqueValue(int byteCount) =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string Hash(byte[] value) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(value));
}

public sealed class Microsoft365SourceConnector
{
    private readonly string _clientId;
    private readonly Uri _organizationsAuthority;
    private readonly Uri _callbackUri;
    private readonly Uri _adminCallbackUri;
    private readonly IReadOnlyList<string> _scopes;
    private readonly ISourceAuthorizationStateStore _stateStore;
    private readonly TimeProvider _clock;

    public Microsoft365SourceConnector(
        BulkScoringOptions options,
        Uri callbackUri,
        Uri adminCallbackUri,
        ISourceAuthorizationStateStore stateStore,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clientId = Required(options.SourceClientId, nameof(options.SourceClientId));
        _organizationsAuthority = ValidateOrganizationsAuthority(options.SourceAuthority);
        _callbackUri = ValidateCallback(callbackUri, options.SourceCallbackPath, nameof(callbackUri));
        _adminCallbackUri = ValidateCallback(
            adminCallbackUri,
            options.SourceAdminConsentCallbackPath,
            nameof(adminCallbackUri));
        if (!_callbackUri.GetLeftPart(UriPartial.Authority).Equals(
                _adminCallbackUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source callback URIs must use the same product origin.");
        if (!BulkScoringOptions.RequiredSourceScopes.SequenceEqual(options.SourceScopes, StringComparer.Ordinal))
            throw new ArgumentException("Only the required delegated source scopes are supported.", nameof(options));

        _scopes = options.SourceScopes
            .Select(QualifyOAuthScope)
            .ToArray();
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _clock = clock ?? TimeProvider.System;
    }

    public Uri OrganizationsAuthority => _organizationsAuthority;
    public Uri CallbackUri => _callbackUri;
    public string ClientId => _clientId;
    public IReadOnlyList<string> Scopes => _scopes;

    public Task<SourceAuthorizationStart> BeginAsync(
        SourceAuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        BeginUserAuthorizationAsync(request, cancellationToken);

    internal async Task<SourceAuthorizationStart> BeginUserAuthorizationAsync(
        SourceAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var stateValue = SourceAuthorizationCryptography.RandomOpaqueValue(32);
        var verifier = SourceAuthorizationCryptography.RandomOpaqueValue(64);
        var expiresAt = _clock.GetUtcNow().Add(request.StateLifetime);
        await _stateStore.StoreAsync(
            ToState(request, stateValue, verifier, SourceAuthorizationStatePurpose.UserAuthorization, expiresAt),
            cancellationToken);

        var endpoint = new Uri(
            _organizationsAuthority.AbsoluteUri.TrimEnd('/') + "/oauth2/v2.0/authorize");
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _callbackUri.AbsoluteUri,
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', _scopes),
            ["state"] = stateValue,
            ["code_challenge"] = SourceAuthorizationCryptography.CreateS256Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        return new SourceAuthorizationStart(
            request.ConnectionId,
            QueryHelpers.AddQueryString(endpoint.AbsoluteUri, query),
            expiresAt);
    }

    internal async Task<SourceAuthorizationStart> BeginAdminConsentAsync(
        SourceAuthorizationState prior,
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(tenantId, out _))
            throw SourceConnectorException.UnsupportedAccount();

        var stateValue = SourceAuthorizationCryptography.RandomOpaqueValue(32);
        var expiresAt = prior.ExpiresAt;
        if (expiresAt <= _clock.GetUtcNow())
            throw SourceConnectorException.InvalidOrExpired();

        await _stateStore.StoreAsync(
            prior with
            {
                StateHash = SourceAuthorizationCryptography.HashState(stateValue),
                CodeVerifier = string.Empty,
                Purpose = SourceAuthorizationStatePurpose.AdminConsent,
            },
            cancellationToken);

        // Use the static tenant admin-consent endpoint. It grants only the
        // delegated permissions declared on the connector registration and
        // avoids tenant-specific v2 dynamic-scope parsing during escalation.
        var endpoint = $"https://login.microsoftonline.com/{tenantId}/adminconsent";
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _clientId,
            ["redirect_uri"] = _adminCallbackUri.AbsoluteUri,
            ["state"] = stateValue,
        };
        return new SourceAuthorizationStart(
            prior.ConnectionId,
            QueryHelpers.AddQueryString(endpoint, query),
            expiresAt);
    }

    internal Task<SourceAuthorizationStart> RestartAfterAdminConsentAsync(
        SourceAuthorizationState adminState,
        CancellationToken cancellationToken)
    {
        var remaining = adminState.ExpiresAt - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            throw SourceConnectorException.InvalidOrExpired();
        return BeginUserAuthorizationAsync(
            new SourceAuthorizationRequest(
                adminState.ConnectionId,
                adminState.ProductActorId,
                adminState.JobId,
                adminState.CanonicalUrlHash,
                adminState.RedirectAfterPath,
                remaining),
            cancellationToken);
    }

    private static SourceAuthorizationState ToState(
        SourceAuthorizationRequest request,
        string stateValue,
        string verifier,
        SourceAuthorizationStatePurpose purpose,
        DateTimeOffset expiresAt) =>
        new(
            SourceAuthorizationCryptography.HashState(stateValue),
            verifier,
            purpose,
            request.ConnectionId,
            request.ProductActorId,
            request.JobId,
            request.CanonicalUrlHash,
            request.RedirectAfterPath,
            expiresAt);

    internal static string QualifyOAuthScope(string scope) =>
        scope is "openid" or "profile" or "offline_access"
            ? scope
            : scope.Contains("://", StringComparison.Ordinal)
                ? scope
                : $"https://graph.microsoft.com/{scope}";

    private static void ValidateRequest(SourceAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Required(request.ConnectionId, nameof(request.ConnectionId));
        Required(request.ProductActorId, nameof(request.ProductActorId));
        Required(request.JobId, nameof(request.JobId));
        Required(request.CanonicalUrlHash, nameof(request.CanonicalUrlHash));
        if (request.StateLifetime <= TimeSpan.Zero || request.StateLifetime > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(request), "Callback state lifetime must be positive and at most 30 minutes.");
        if (!request.RedirectAfterPath.StartsWith("/", StringComparison.Ordinal)
            || Uri.TryCreate(request.RedirectAfterPath, UriKind.Absolute, out _)
            || request.RedirectAfterPath.Contains('\r')
            || request.RedirectAfterPath.Contains('\n'))
            throw new ArgumentException("The return path must be a local product path.", nameof(request));
    }

    private static Uri ValidateOrganizationsAuthority(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.TrimEnd('/').Equals("/organizations", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The source authority must be the Microsoft commercial-cloud /organizations authority.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/'));
    }

    private static Uri ValidateCallback(Uri uri, string expectedPath, string argument)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort)
            throw new ArgumentException("The source callback URI must be the exact registered HTTPS URI.", argument);
        return uri;
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A required source connector value is missing.", name);
}
