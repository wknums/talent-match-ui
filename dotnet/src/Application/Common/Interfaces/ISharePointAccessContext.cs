using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.Common.Interfaces;

// Uses only the connection- or request-bound delegated Microsoft 365 authorization.
public interface ISharePointAccessContext
{
    string SourceTenantId => string.Empty;

    Task<string> GetGraphTokenAsync(
        SourceCallContext context,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<DelegatedGraphAccess> GetGraphAccessAsync(
        SourceCallContext context,
        CancellationToken cancellationToken = default) =>
        GetLegacyAccessAsync(this, context, cancellationToken);

    private static async Task<DelegatedGraphAccess> GetLegacyAccessAsync(
        ISharePointAccessContext access,
        SourceCallContext context,
        CancellationToken cancellationToken) =>
        new(
            await access.GetGraphTokenAsync(context, cancellationToken),
            access.SourceTenantId);
}

public sealed record DelegatedGraphAccess(string AccessToken, string SourceTenantId);

public sealed record AuthorizationSecret(byte[] WrappedDataKey, string KeyVersion);

public interface IAuthorizationKeyWrapper
{
    string CurrentKeyVersion { get; }

    Task<AuthorizationSecret> WrapAsync(byte[] dataKey, CancellationToken cancellationToken = default);

    Task<byte[]> UnwrapAsync(byte[] wrappedDataKey, string keyVersion, CancellationToken cancellationToken = default);
}

// Holds each request's protected authorization-code cache. A verified pending connection transfers
// and re-encrypts its cache for exactly one request; no product token or alternate identity is used.
public interface IRequestAuthorizationStore
{
    // Transfers one verified pending connection into a request-bound delegated cache.
    Task<BulkRequestAuthorization> TransferToRequestAsync(
        string requestId,
        string connectionId,
        string recruiterActorId,
        string jobId,
        CancellationToken cancellationToken = default);

    // In-memory stores use this to participate in the same rollback contract as database stores.
    // The production transfer is rolled back by the ambient database transaction.
    Task RollbackTransferAsync(
        BulkRequestAuthorization authorization,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // Throws SourceAuthorizationLapsedException when no token can be obtained (lapse, not a transient error).
    Task<string> AcquireGraphTokenAsync(string requestId, CancellationToken cancellationToken = default);

    Task MarkLapsedAsync(string requestId, string reason, CancellationToken cancellationToken = default);

    Task DiscardAsync(string requestId, CancellationToken cancellationToken = default);
}
