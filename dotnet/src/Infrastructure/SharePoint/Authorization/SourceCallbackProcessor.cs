using Microsoft.Identity.Client;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

public interface ISourceAuthorizationCodeClient
{
    Task<SourceIdentityResult> RedeemAsync(
        SourceCodeRedemptionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SourceCodeRedemptionRequest(
    string ClientId,
    Uri Authority,
    IReadOnlyList<string> Scopes,
    string AuthorizationCode,
    string CodeVerifier,
    Uri RedirectUri,
    string ClientAssertion);

public sealed record SourceIdentityResult(
    string TenantId,
    string AccountTenantId,
    string AccountId,
    bool IsOrganizational,
    string CloudHost,
    byte[] TokenCache);

public sealed record SourceAuthorizationCallback(
    string? Code,
    string State,
    string? Error = null,
    string? ErrorDescription = null);

public sealed record SourceAdminConsentCallback(
    string State,
    bool Granted,
    string? ReturnedTenant = null,
    string? Error = null);

public enum SourceCallbackOutcomeKind
{
    Authorized,
    AdminConsentRequired,
    AuthorizationRestarted,
}

public sealed record SourceCallbackOutcome(
    SourceCallbackOutcomeKind Kind,
    string ConnectionId,
    string ProductActorId,
    string RedirectAfterPath,
    string? SourceTenantId = null,
    string? SourceAccountBindingHash = null,
    byte[]? TokenCache = null,
    string? SafeCode = null,
    string? AdminConsentUrl = null,
    string? AuthorizationUrl = null);

public sealed class SourceConnectorException : Exception
{
    private SourceConnectorException(string code, string safeMessage)
        : base(safeMessage) =>
        Code = code;

    public string Code { get; }

    internal static SourceConnectorException InvalidOrExpired() =>
        new("source_connection_invalid_or_expired", "The source connection is invalid or has expired.");

    internal static SourceConnectorException AccessDenied() =>
        new("source_access_denied", "Microsoft 365 source access was not granted.");

    internal static SourceConnectorException UnsupportedAccount() =>
        new("source_unsupported_account", "Only organizational Microsoft 365 accounts are supported.");

    internal static SourceConnectorException UnsupportedCloud() =>
        new("source_unsupported_cloud", "Only Microsoft 365 commercial-cloud accounts are supported.");
}

public sealed class SourceAdminConsentRequiredException(string tenantId) : Exception
{
    public string TenantId { get; } = tenantId;
}

public sealed class SourceCallbackProcessor(
    Microsoft365SourceConnector connector,
    ISourceAuthorizationStateStore stateStore,
    ISourceAuthorizationCodeClient codeClient,
    IClientAssertionProvider assertionProvider,
    TimeProvider? clock = null)
{
    private const string ConsumerTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<SourceCallbackOutcome> ProcessAsync(
        SourceAuthorizationCallback callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var pending = await TakeAsync(
            callback.State,
            SourceAuthorizationStatePurpose.UserAuthorization,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(callback.Error))
            throw SourceConnectorException.AccessDenied();
        if (string.IsNullOrWhiteSpace(callback.Code))
            throw SourceConnectorException.InvalidOrExpired();

        SourceIdentityResult identity;
        try
        {
            var assertion = await assertionProvider.GetClientAssertionAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(assertion))
                throw new InvalidOperationException();
            identity = await codeClient.RedeemAsync(
                new SourceCodeRedemptionRequest(
                    connector.ClientId,
                    connector.OrganizationsAuthority,
                    connector.Scopes,
                    callback.Code,
                    pending.CodeVerifier,
                    connector.CallbackUri,
                    assertion),
                cancellationToken);
        }
        catch (SourceAdminConsentRequiredException consent)
        {
            var admin = await connector.BeginAdminConsentAsync(pending, consent.TenantId, cancellationToken);
            return new SourceCallbackOutcome(
                SourceCallbackOutcomeKind.AdminConsentRequired,
                pending.ConnectionId,
                pending.ProductActorId,
                pending.RedirectAfterPath,
                SafeCode: "source_admin_consent_required",
                AdminConsentUrl: admin.AuthorizationUrl);
        }
        catch (SourceConnectorException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw SourceConnectorException.InvalidOrExpired();
        }

        ValidateIdentity(identity);
        return new SourceCallbackOutcome(
            SourceCallbackOutcomeKind.Authorized,
            pending.ConnectionId,
            pending.ProductActorId,
            pending.RedirectAfterPath,
            identity.TenantId,
            SourceAuthorizationCryptography.HashAccount(identity.TenantId, identity.AccountId),
            identity.TokenCache.ToArray());
    }

    public async Task<SourceCallbackOutcome> ProcessAdminConsentAsync(
        SourceAdminConsentCallback callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var pending = await TakeAsync(
            callback.State,
            SourceAuthorizationStatePurpose.AdminConsent,
            cancellationToken);
        if (!callback.Granted || !string.IsNullOrWhiteSpace(callback.Error))
            throw SourceConnectorException.AccessDenied();

        // ReturnedTenant is intentionally ignored. Tenant identity is established only by token redemption.
        var restart = await connector.RestartAfterAdminConsentAsync(pending, cancellationToken);
        return new SourceCallbackOutcome(
            SourceCallbackOutcomeKind.AuthorizationRestarted,
            pending.ConnectionId,
            pending.ProductActorId,
            pending.RedirectAfterPath,
            AuthorizationUrl: restart.AuthorizationUrl);
    }

    private async Task<SourceAuthorizationState> TakeAsync(
        string? rawState,
        SourceAuthorizationStatePurpose expectedPurpose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawState) || rawState.Length > 512)
            throw SourceConnectorException.InvalidOrExpired();

        SourceAuthorizationState? pending;
        try
        {
            pending = await stateStore.TakeAsync(
                SourceAuthorizationCryptography.HashState(rawState),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw SourceConnectorException.InvalidOrExpired();
        }

        if (pending is null
            || pending.Purpose != expectedPurpose
            || pending.ExpiresAt <= _clock.GetUtcNow())
            throw SourceConnectorException.InvalidOrExpired();
        return pending;
    }

    internal static void ValidateIdentity(SourceIdentityResult identity)
    {
        if (identity is null
            || !identity.IsOrganizational
            || !Guid.TryParse(identity.TenantId, out _)
            || !Guid.TryParse(identity.AccountTenantId, out _)
            || identity.TenantId.Equals(ConsumerTenant, StringComparison.OrdinalIgnoreCase)
            || !identity.TenantId.Equals(identity.AccountTenantId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(identity.AccountId)
            || identity.TokenCache is not { Length: > 0 })
            throw SourceConnectorException.UnsupportedAccount();

        if (!identity.CloudHost.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            && !identity.CloudHost.Equals("login.windows.net", StringComparison.OrdinalIgnoreCase))
            throw SourceConnectorException.UnsupportedCloud();
    }
}

public sealed class MsalSourceAuthorizationCodeClient : ISourceAuthorizationCodeClient
{
    public async Task<SourceIdentityResult> RedeemAsync(
        SourceCodeRedemptionRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] cache = [];
        var application = ConfidentialClientApplicationBuilder
            .Create(request.ClientId)
            .WithAuthority(request.Authority.AbsoluteUri)
            .WithRedirectUri(request.RedirectUri.AbsoluteUri)
            .WithClientAssertion(
                (AssertionRequestOptions _) => Task.FromResult(request.ClientAssertion))
            .Build();
        application.UserTokenCache.SetBeforeAccess(args =>
        {
            if (cache.Length > 0)
                args.TokenCache.DeserializeMsalV3(cache, shouldClearExistingCache: true);
        });
        application.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
                cache = args.TokenCache.SerializeMsalV3();
        });

        var result = await application
            .AcquireTokenByAuthorizationCode(request.Scopes, request.AuthorizationCode)
            .WithPkceCodeVerifier(request.CodeVerifier)
            .ExecuteAsync(cancellationToken);
        var tenantId = result.TenantId;
        var accountTenant = result.Account?.HomeAccountId?.TenantId ?? string.Empty;
        var accountId = result.Account?.HomeAccountId?.Identifier
            ?? result.Account?.Username
            ?? string.Empty;
        var cloudHost = result.Account?.Environment ?? request.Authority.Host;
        var organizational = !tenantId.Equals(
            "9188040d-6c67-4c5b-b112-36a304b66dad",
            StringComparison.OrdinalIgnoreCase);
        return new SourceIdentityResult(
            tenantId,
            accountTenant,
            accountId,
            organizational,
            cloudHost,
            cache);
    }
}
