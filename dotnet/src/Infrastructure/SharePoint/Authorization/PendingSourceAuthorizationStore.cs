using System.Security.Cryptography;
using System.Text.Json;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

public sealed record PendingSourceAuthorizationBinding(
    string ConnectionId,
    string ProductActorId,
    string JobId,
    string CanonicalUrlHash)
{
    internal string AssociatedData => AuthorizationEnvelope.PendingBinding(
        ConnectionId,
        ProductActorId,
        JobId,
        CanonicalUrlHash);
}

public sealed record PendingSourceAuthorizationCache(
    string SourceTenantId,
    string SourceAccountBindingHash,
    byte[] TokenCache);

public sealed class PendingSourceAuthorizationException(string code, Exception? inner = null)
    : Exception("The source connection is invalid or has expired.", inner)
{
    public string Code { get; } = code;
}

internal sealed record AuthorizationCodeCacheDto(
    string SourceTenantId,
    string SourceAccountBindingHash,
    string Cache);

internal sealed record RequestAuthorizationCodeCacheDto(
    string ProductActorId,
    string SourceTenantId,
    string SourceAccountBindingHash,
    string SourceDriveId,
    string SourceFolderItemId,
    string Cache);

public sealed class PendingSourceAuthorizationStore(
    IBulkSourceConnectionRepository repository,
    IAuthorizationKeyWrapper keyWrapper,
    BulkScoringOptions options,
    TimeProvider clock)
{
    public async Task StoreAuthorizedCacheAsync(
        PendingSourceAuthorizationBinding binding,
        string expectedStateHash,
        string sourceTenantId,
        string sourceAccountBindingHash,
        byte[] tokenCache,
        CancellationToken cancellationToken = default)
    {
        Validate(binding);
        Required(expectedStateHash);
        Required(sourceTenantId);
        Required(sourceAccountBindingHash);
        if (tokenCache is not { Length: > 0 })
            throw new ArgumentException("An MSAL cache is required.", nameof(tokenCache));

        var connection = await GetActiveAsync(binding, cancellationToken);
        if (connection.Status != BulkSourceConnectionStatus.Authorizing)
            throw Invalid("source_connection_not_authorizing");

        var cache = new PendingSourceAuthorizationCache(
            sourceTenantId,
            sourceAccountBindingHash,
            tokenCache.ToArray());
        var sealedCache = await SealAsync(cache, binding.AssociatedData, cancellationToken);
        try
        {
            connection.CompleteAuthorization(
                expectedStateHash,
                sourceTenantId,
                sourceAccountBindingHash,
                sealedCache.Payload,
                sealedCache.Wrapped.WrappedDataKey,
                sealedCache.Wrapped.KeyVersion,
                clock.GetUtcNow().UtcDateTime,
                options.ConnectionIdleTtl,
                options.ConnectionAbsoluteTtl);
            await repository.SaveAsync(connection, cancellationToken);
        }
        catch
        {
            AuthorizationEnvelope.Discard(sealedCache.Payload, sealedCache.Wrapped.WrappedDataKey);
            throw;
        }
        finally
        {
            AuthorizationEnvelope.Discard(cache.TokenCache);
        }
    }

    public async Task<PendingSourceAuthorizationCache> OpenAsync(
        PendingSourceAuthorizationBinding binding,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetActiveAsync(binding, cancellationToken);
        if (connection.Status is not (BulkSourceConnectionStatus.Authorized or BulkSourceConnectionStatus.Verified))
            throw Invalid("source_connection_not_authorized");

        var cache = await OpenCoreAsync(connection, binding.AssociatedData, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        if (!string.Equals(connection.KeyVersion, keyWrapper.CurrentKeyVersion, StringComparison.Ordinal))
        {
            var replacement = await SealAsync(cache, binding.AssociatedData, cancellationToken);
            AuthorizationEnvelope.Discard(connection.ProtectedPayload, connection.WrappedDataKey);
            connection.ProtectedPayload = replacement.Payload;
            connection.WrappedDataKey = replacement.Wrapped.WrappedDataKey;
            connection.KeyVersion = replacement.Wrapped.KeyVersion;
        }
        connection.Touch(now, options.ConnectionIdleTtl, options.ConnectionAbsoluteTtl);
        await repository.SaveAsync(connection, cancellationToken);
        return cache;
    }

    public async Task TouchAsync(
        PendingSourceAuthorizationBinding binding,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetActiveAsync(binding, cancellationToken);
        connection.Touch(
            clock.GetUtcNow().UtcDateTime,
            options.ConnectionIdleTtl,
            options.ConnectionAbsoluteTtl);
        await repository.SaveAsync(connection, cancellationToken);
    }

    public async Task AbandonAsync(
        PendingSourceAuthorizationBinding binding,
        CancellationToken cancellationToken = default)
    {
        _ = await GetActiveAsync(binding, cancellationToken);
        if (!await repository.AbandonAsync(
                binding.ConnectionId,
                binding.ProductActorId,
                binding.JobId,
                clock.GetUtcNow().UtcDateTime,
                cancellationToken))
            throw Invalid();
    }

    public async Task ClearCallbackFailureAsync(
        PendingSourceAuthorizationBinding binding,
        string safeFailureCode,
        CancellationToken cancellationToken = default)
    {
        Required(safeFailureCode);
        var connection = await GetActiveAsync(binding, cancellationToken);
        connection.Fail(safeFailureCode, clock.GetUtcNow().UtcDateTime);
        await repository.SaveAsync(connection, cancellationToken);
    }

    public async Task<BulkRequestAuthorization> TransferToRequestAsync(
        PendingSourceAuthorizationBinding binding,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        Required(requestId);
        var connection = await GetActiveAsync(binding, cancellationToken);
        if (connection.Status != BulkSourceConnectionStatus.Verified
            || string.IsNullOrWhiteSpace(connection.SourceDriveId)
            || string.IsNullOrWhiteSpace(connection.SourceFolderItemId))
            throw Invalid("source_connection_not_verified");

        var cache = await OpenCoreAsync(connection, binding.AssociatedData, cancellationToken);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new RequestAuthorizationCodeCacheDto(
            binding.ProductActorId,
            cache.SourceTenantId,
            cache.SourceAccountBindingHash,
            connection.SourceDriveId,
            connection.SourceFolderItemId,
            Convert.ToBase64String(cache.TokenCache)));
        var (requestPayload, requestDataKey) = AuthorizationEnvelope.Seal(
            plaintext,
            AuthorizationEnvelope.RequestBinding(requestId));
        AuthorizationSecret requestWrapped;
        try
        {
            requestWrapped = await keyWrapper.WrapAsync(requestDataKey, cancellationToken);
        }
        catch
        {
            AuthorizationEnvelope.Discard(requestPayload);
            throw;
        }
        finally
        {
            AuthorizationEnvelope.Discard(plaintext, requestDataKey);
        }
        try
        {
            return await repository.TransferToRequestAsync(
                binding.ConnectionId,
                binding.ProductActorId,
                binding.JobId,
                binding.CanonicalUrlHash,
                requestId,
                requestPayload,
                requestWrapped.WrappedDataKey,
                requestWrapped.KeyVersion,
                clock.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            AuthorizationEnvelope.Discard(
                requestPayload,
                requestWrapped.WrappedDataKey);
            throw Invalid(ex.Message, ex);
        }
        finally
        {
            AuthorizationEnvelope.Discard(cache.TokenCache);
        }
    }

    public async Task<BulkRequestAuthorization> TransferToRequestAsync(
        string connectionId,
        string productActorId,
        string jobId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var connection = await repository.GetForTransferAsync(
            connectionId,
            productActorId,
            jobId,
            cancellationToken);
        if (connection is null)
            throw Invalid();
        return await TransferToRequestAsync(
            new PendingSourceAuthorizationBinding(
                connection.Id,
                connection.ProductActorId,
                connection.JobId,
                connection.CanonicalUrlHash),
            requestId,
            cancellationToken);
    }

    private async Task<BulkSourceConnection> GetActiveAsync(
        PendingSourceAuthorizationBinding binding,
        CancellationToken cancellationToken)
    {
        Validate(binding);
        var connection = await repository.GetBoundAsync(
            binding.ConnectionId,
            binding.ProductActorId,
            binding.JobId,
            binding.CanonicalUrlHash,
            cancellationToken);
        if (connection is null || !connection.IsPending)
            throw Invalid();

        var now = clock.GetUtcNow().UtcDateTime;
        if (connection.IsExpired(now))
        {
            await repository.ExpireAsync(connection.Id, now, cancellationToken);
            throw Invalid();
        }
        return connection;
    }

    private async Task<PendingSourceAuthorizationCache> OpenCoreAsync(
        BulkSourceConnection connection,
        string associatedData,
        CancellationToken cancellationToken)
    {
        if (connection.ProtectedPayload is not { Length: > 0 }
            || connection.WrappedDataKey is not { Length: > 0 }
            || string.IsNullOrWhiteSpace(connection.KeyVersion))
            throw Invalid("source_connection_authorization_missing");

        byte[]? dataKey = null;
        byte[]? plaintext = null;
        try
        {
            dataKey = await keyWrapper.UnwrapAsync(
                connection.WrappedDataKey,
                connection.KeyVersion,
                cancellationToken);
            plaintext = AuthorizationEnvelope.Open(
                connection.ProtectedPayload,
                dataKey,
                associatedData);
            var dto = JsonSerializer.Deserialize<AuthorizationCodeCacheDto>(plaintext);
            if (dto is null
                || dto.SourceTenantId != connection.SourceTenantId
                || dto.SourceAccountBindingHash != connection.SourceAccountBindingHash)
                throw new CryptographicException("Authorization binding mismatch.");
            var cache = Convert.FromBase64String(dto.Cache);
            if (cache.Length == 0)
                throw new CryptographicException("Authorization cache is empty.");
            return new PendingSourceAuthorizationCache(
                dto.SourceTenantId,
                dto.SourceAccountBindingHash,
                cache);
        }
        catch (PendingSourceAuthorizationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            throw Invalid("source_connection_authorization_unreadable", ex);
        }
        finally
        {
            AuthorizationEnvelope.Discard(dataKey, plaintext);
        }
    }

    private async Task<(byte[] Payload, AuthorizationSecret Wrapped)> SealAsync(
        PendingSourceAuthorizationCache cache,
        string associatedData,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new AuthorizationCodeCacheDto(
            cache.SourceTenantId,
            cache.SourceAccountBindingHash,
            Convert.ToBase64String(cache.TokenCache)));
        var (payload, dataKey) = AuthorizationEnvelope.Seal(plaintext, associatedData);
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

    private static void Validate(PendingSourceAuthorizationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Required(binding.ConnectionId);
        Required(binding.ProductActorId);
        Required(binding.JobId);
        Required(binding.CanonicalUrlHash);
    }

    private static string Required(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An authorization binding value is required.")
            : value;

    private static PendingSourceAuthorizationException Invalid(
        string code = "source_connection_invalid_or_expired",
        Exception? inner = null) =>
        new(code, inner);
}
