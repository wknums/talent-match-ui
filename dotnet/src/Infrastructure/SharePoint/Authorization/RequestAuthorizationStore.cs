using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Microsoft.Identity.Client;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

public sealed class RequestAuthorizationStore(
    IBulkRequestAuthorizationRepository repository,
    IAuthorizationKeyWrapper keyWrapper,
    BulkScoringOptions options,
    TimeProvider clock,
    Func<string, IConfidentialClientApplication>? clientFactory = null,
    PendingSourceAuthorizationStore? pendingAuthorizations = null) : IRequestAuthorizationStore
{
    private static readonly TimeSpan TokenReuseMargin = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, CachedToken> Tokens = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private readonly string[] _graphScopes = options.SourceScopes
        .Where(scope => scope is not ("openid" or "profile" or "offline_access"))
        .Select(scope => scope.Contains("://", StringComparison.Ordinal)
            ? scope
            : $"https://graph.microsoft.com/{scope}")
        .ToArray();

    private sealed record CachedToken(
        string Token,
        DateTimeOffset ExpiresOn,
        string ProductActorId,
        string SourceTenantId,
        string SourceAccountBindingHash,
        string SourceDriveId,
        string SourceFolderItemId)
    {
        public bool IsUsableFor(BulkRequestAuthorization record, DateTimeOffset now) =>
            ExpiresOn > now + TokenReuseMargin
            && ProductActorId == record.ProductActorId
            && SourceTenantId == record.SourceTenantId
            && SourceAccountBindingHash == record.SourceAccountBindingHash
            && SourceDriveId == record.SourceDriveId
            && SourceFolderItemId == record.SourceFolderItemId;
    }

    public Task<BulkRequestAuthorization> TransferToRequestAsync(
        string requestId,
        string connectionId,
        string recruiterActorId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new AuthorizationRequiredException(
                "Complete the separate Microsoft 365 source connection before confirming the request.");
        if (pendingAuthorizations is null)
            throw new AuthorizationRequiredException(
                "Complete the separate Microsoft 365 source connection before confirming the request.");
        return pendingAuthorizations.TransferToRequestAsync(
            connectionId,
            recruiterActorId,
            jobId,
            requestId,
            cancellationToken);
    }

    public async Task<string> AcquireGraphTokenAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var record = await GetUsableRecordAsync(requestId, cancellationToken);
        if (Tokens.TryGetValue(requestId, out var cached)
            && cached.IsUsableFor(record, clock.GetUtcNow()))
            return cached.Token;

        var gate = Locks.GetOrAdd(requestId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            record = await GetUsableRecordAsync(requestId, cancellationToken);
            if (Tokens.TryGetValue(requestId, out cached)
                && cached.IsUsableFor(record, clock.GetUtcNow()))
                return cached.Token;

            var now = clock.GetUtcNow().UtcDateTime;
            var cache = await OpenAsync(record, cancellationToken);
            try
            {
                var app = CreateClient(record.SourceTenantId);
                app.UserTokenCache.SetBeforeAccess(args =>
                    args.TokenCache.DeserializeMsalV3(cache, shouldClearExistingCache: true));
                byte[]? rotated = null;
                app.UserTokenCache.SetAfterAccess(args =>
                {
                    if (args.HasStateChanged)
                        rotated = args.TokenCache.SerializeMsalV3();
                });

                AuthenticationResult result;
                try
                {
                    var accounts = await app.GetAccountsAsync();
                    var account = accounts.FirstOrDefault(candidate =>
                        candidate.HomeAccountId?.TenantId.Equals(
                            record.SourceTenantId,
                            StringComparison.OrdinalIgnoreCase) == true
                        && SourceAuthorizationCryptography.HashAccount(
                            record.SourceTenantId,
                            candidate.HomeAccountId.Identifier)
                            .Equals(record.SourceAccountBindingHash, StringComparison.Ordinal));
                    if (account is null)
                        throw Lapse(requestId, "authorization_binding_mismatch");

                    result = await app.AcquireTokenSilent(_graphScopes, account)
                        .WithForceRefresh(false)
                        .ExecuteAsync(cancellationToken);
                }
                catch (SourceAuthorizationLapsedException)
                {
                    throw;
                }
                catch (MsalUiRequiredException ex)
                {
                    throw Lapse(requestId, AuthorizationLapse.Reason(ex), ex);
                }
                catch (MsalServiceException ex) when (!AuthorizationLapse.IsTransient(ex))
                {
                    throw Lapse(requestId, AuthorizationLapse.Reason(ex), ex);
                }
                catch (MsalServiceException ex)
                {
                    throw new SourceTransientException(
                        "The identity provider is temporarily unavailable.",
                        inner: ex);
                }
                catch (MsalClientException ex) when (
                    ex.ErrorCode is MsalError.RequestTimeout or MsalError.UnknownError)
                {
                    throw new SourceTransientException(
                        "The identity provider could not be reached.",
                        inner: ex);
                }

                if (rotated is not null
                    || !string.Equals(
                        record.KeyVersion,
                        keyWrapper.CurrentKeyVersion,
                        StringComparison.Ordinal))
                {
                    var material = rotated ?? cache;
                    var (payload, wrapped) = await SealAsync(record, material, cancellationToken);
                    record.ReplaceProtectedMaterial(
                        payload,
                        wrapped.WrappedDataKey,
                        wrapped.KeyVersion,
                        now);
                }
                else
                {
                    record.RecordUse(now);
                }
                await repository.SaveAsync(record, cancellationToken);

                Tokens[requestId] = new CachedToken(
                    result.AccessToken,
                    result.ExpiresOn,
                    record.ProductActorId,
                    record.SourceTenantId,
                    record.SourceAccountBindingHash,
                    record.SourceDriveId,
                    record.SourceFolderItemId);
                return result.AccessToken;
            }
            finally
            {
                AuthorizationEnvelope.Discard(cache);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BulkRequestAuthorization> GetUsableRecordAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        var record = await repository.GetAsync(requestId, cancellationToken)
            ?? throw Lapse(requestId, "authorization_missing");
        ValidateRecord(record, requestId);
        if (!record.IsActive)
            throw Lapse(requestId, "not_active");
        if (record.IsOlderThan(options.AuthorizationMaxAge, clock.GetUtcNow().UtcDateTime))
            throw Lapse(requestId, "max_age_exceeded");
        return record;
    }

    public async Task MarkLapsedAsync(
        string requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Forget(requestId);
        var record = await repository.GetAsync(requestId, cancellationToken);
        if (record is not { IsActive: true })
            return;
        var now = clock.GetUtcNow().UtcDateTime;
        switch (reason)
        {
            case "session_revoked":
                record.MarkRevoked(reason, now);
                break;
            case "account_disabled":
            case "conditional_access":
            case "access_lost":
                record.MarkAccessLost(reason, now);
                break;
            default:
                record.MarkLapsed(reason, now);
                break;
        }
        await repository.SaveAsync(record, cancellationToken);
    }

    public async Task DiscardAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        Forget(requestId);
        var record = await repository.GetAsync(requestId, cancellationToken);
        if (record is null
            || record.Status is BulkAuthorizationStatus.Discarded
                or BulkAuthorizationStatus.UnavailableHistorical)
            return;
        record.Discard(clock.GetUtcNow().UtcDateTime);
        await repository.SaveAsync(record, cancellationToken);
    }

    private static void ValidateRecord(BulkRequestAuthorization record, string requestId)
    {
        if (record.RequestId != requestId
            || string.IsNullOrWhiteSpace(record.ProductActorId)
            || string.IsNullOrWhiteSpace(record.SourceTenantId)
            || string.IsNullOrWhiteSpace(record.SourceAccountBindingHash)
            || string.IsNullOrWhiteSpace(record.SourceDriveId)
            || string.IsNullOrWhiteSpace(record.SourceFolderItemId))
            throw Lapse(requestId, "authorization_binding_mismatch");
    }

    private IConfidentialClientApplication CreateClient(string tenantId) =>
        clientFactory is not null ? clientFactory(tenantId) : DefaultClient(tenantId);

    private IConfidentialClientApplication DefaultClient(string tenantId)
    {
        var client = options.SourceClientId
            ?? throw new InvalidOperationException("MICROSOFT365_SOURCE_CLIENT_ID is required.");
        var configured = options.SourceAuthority
            ?? throw new InvalidOperationException("MICROSOFT365_SOURCE_AUTHORITY is required.");
        var authority = new Uri(new Uri(configured.TrimEnd('/') + "/"), $"../{tenantId}").AbsoluteUri;
        return ConfidentialClientApplicationBuilder.Create(client)
            .WithAuthority(authority, validateAuthority: true)
            .WithClientAssertion(
                (AssertionRequestOptions request) =>
                    ManagedIdentityClientAssertion.GetAsync(request.CancellationToken))
            .Build();
    }

    private async Task<byte[]> OpenAsync(
        BulkRequestAuthorization record,
        CancellationToken cancellationToken)
    {
        byte[]? dataKey = null;
        byte[]? plaintext = null;
        try
        {
            dataKey = await keyWrapper.UnwrapAsync(
                record.WrappedDataKey!,
                record.KeyVersion,
                cancellationToken);
            plaintext = AuthorizationEnvelope.Open(
                record.Payload!,
                dataKey,
                AuthorizationEnvelope.RequestBinding(record.RequestId));
            var dto = JsonSerializer.Deserialize<RequestAuthorizationCodeCacheDto>(plaintext);
            if (dto is null
                || dto.ProductActorId != record.ProductActorId
                || dto.SourceTenantId != record.SourceTenantId
                || dto.SourceAccountBindingHash != record.SourceAccountBindingHash
                || dto.SourceDriveId != record.SourceDriveId
                || dto.SourceFolderItemId != record.SourceFolderItemId)
                throw new CryptographicException("Authorization binding mismatch.");
            var cache = Convert.FromBase64String(dto.Cache);
            if (cache.Length == 0)
                throw new CryptographicException("Authorization cache is empty.");
            return cache;
        }
        catch (CryptographicException ex)
        {
            throw Lapse(record.RequestId, "authorization_unreadable", ex);
        }
        catch (RequestFailedException ex) when (ex.Status is 400 or 404)
        {
            throw Lapse(record.RequestId, "key_unavailable", ex);
        }
        catch (RequestFailedException ex)
        {
            throw new SourceTransientException(
                "The key service is temporarily unavailable.",
                inner: ex);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NullReferenceException)
        {
            throw Lapse(record.RequestId, "authorization_unreadable", ex);
        }
        finally
        {
            AuthorizationEnvelope.Discard(dataKey, plaintext);
        }
    }

    private async Task<(byte[] Payload, AuthorizationSecret Wrapped)> SealAsync(
        BulkRequestAuthorization record,
        byte[] cache,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new RequestAuthorizationCodeCacheDto(
            record.ProductActorId,
            record.SourceTenantId,
            record.SourceAccountBindingHash,
            record.SourceDriveId,
            record.SourceFolderItemId,
            Convert.ToBase64String(cache)));
        var (payload, dataKey) = AuthorizationEnvelope.Seal(
            plaintext,
            AuthorizationEnvelope.RequestBinding(record.RequestId));
        try
        {
            return (payload, await keyWrapper.WrapAsync(dataKey, cancellationToken));
        }
        catch
        {
            AuthorizationEnvelope.Discard(payload);
            throw;
        }
        finally
        {
            AuthorizationEnvelope.Discard(dataKey, plaintext);
        }
    }

    private static void Forget(string requestId)
    {
        Tokens.TryRemove(requestId, out _);
        Locks.TryRemove(requestId, out _);
    }

    private static SourceAuthorizationLapsedException Lapse(
        string requestId,
        string reason,
        Exception? inner = null)
    {
        Forget(requestId);
        return new SourceAuthorizationLapsedException(reason, inner);
    }
}

public static class AuthorizationLapse
{
    public static string Reason(Exception exception)
    {
        var code = Regex.Match(exception.Message, @"AADSTS(\d+)") is { Success: true } match
            ? match.Groups[1].Value
            : null;
        return code switch
        {
            "50173" or "70043" or "700082" => "session_revoked",
            "130507" or "50057" => "account_disabled",
            "53003" or "53000" or "53001" or "530032" => "conditional_access",
            "130504" or "50132" or "50133" => "session_expired",
            _ => "interaction_required",
        };
    }

    public static bool IsTransient(MsalServiceException exception) =>
        exception.IsRetryable || exception.StatusCode is 408 or 429 or >= 500;
}
