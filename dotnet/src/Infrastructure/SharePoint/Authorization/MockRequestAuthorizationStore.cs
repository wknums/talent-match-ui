using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

// Shared switch for tests and local demos: lapse a request's authorization on demand.
public sealed class MockAuthorizationControl
{
    private readonly ConcurrentDictionary<string, string> _lapsed = new();

    public void Lapse(string requestId, string reason = "token_unavailable") => _lapsed[requestId] = reason;

    public bool TryGetLapse(string requestId, out string reason) => _lapsed.TryGetValue(requestId, out reason!);
}

// Mock-mode authorization store: real envelope encryption and lifecycle, but no identity provider.
// It lets the full workflow (including a lapse) run without a tenant.
public sealed class MockRequestAuthorizationStore(
    IBulkRequestAuthorizationRepository repository,
    IBulkSourceConnectionRepository connections,
    IAuthorizationKeyWrapper keyWrapper,
    MockAuthorizationControl control,
    BulkScoringOptions options,
    TimeProvider clock) : IRequestAuthorizationStore
{
    public async Task<BulkRequestAuthorization> TransferToRequestAsync(
        string requestId,
        string connectionId,
        string recruiterActorId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new { actor = recruiterActorId, mock = true });
        var (payload, dataKey) = AuthorizationEnvelope.Seal(plaintext, requestId);
        var wrapped = await keyWrapper.WrapAsync(dataKey, cancellationToken);
        Array.Clear(dataKey);
        var connection = await connections.GetForTransferAsync(
            connectionId,
            recruiterActorId,
            jobId,
            cancellationToken)
            ?? throw new AuthorizationRequiredException(
                "Complete the separate Microsoft 365 source connection before confirming the request.");
        return await connections.TransferToRequestAsync(
            connectionId,
            recruiterActorId,
            jobId,
            connection.CanonicalUrlHash,
            requestId,
            payload,
            wrapped.WrappedDataKey,
            wrapped.KeyVersion,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
    }

    public async Task<string> AcquireGraphTokenAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var record = await repository.GetAsync(requestId, cancellationToken)
            ?? throw new SourceAuthorizationLapsedException("authorization_missing");

        if (!record.IsActive) throw new SourceAuthorizationLapsedException("not_active");
        if (control.TryGetLapse(requestId, out var forced)) throw new SourceAuthorizationLapsedException(forced);
        if (record.IsOlderThan(options.AuthorizationMaxAge, now)) throw new SourceAuthorizationLapsedException("max_age_exceeded");

        var dataKey = await UnwrapAsync(record, cancellationToken);
        try
        {
            AuthorizationEnvelope.Open(record.Payload!, dataKey, requestId);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Tampered or undecryptable material is permanent, not a transient fault: the request must be re-authorized.
            throw new SourceAuthorizationLapsedException("authorization_unreadable");
        }
        finally
        {
            Array.Clear(dataKey);
        }

        record.RecordUse(now);
        await repository.SaveAsync(record, cancellationToken);
        return "mock-delegated-token:" + record.ProductActorId;
    }

    private async Task<byte[]> UnwrapAsync(BulkRequestAuthorization record, CancellationToken cancellationToken)
    {
        try
        {
            return await keyWrapper.UnwrapAsync(record.WrappedDataKey!, record.KeyVersion, cancellationToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new SourceAuthorizationLapsedException("authorization_unreadable");
        }
    }

    public async Task MarkLapsedAsync(string requestId, string reason, CancellationToken cancellationToken = default)
    {
        var record = await repository.GetAsync(requestId, cancellationToken);
        if (record is not { IsActive: true }) return;
        record.MarkLapsed(reason, clock.GetUtcNow().UtcDateTime);
        await repository.SaveAsync(record, cancellationToken);
    }

    public async Task DiscardAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var record = await repository.GetAsync(requestId, cancellationToken);
        if (record is null || record.Status == BulkAuthorizationStatus.Discarded) return;
        record.Discard(clock.GetUtcNow().UtcDateTime);
        await repository.SaveAsync(record, cancellationToken);
    }
}

// Mock-mode access context: no tokens leave the process, but the delegated background path still
// goes through the request's authorization so a lapse is observable.
public sealed class MockSharePointAccessContext(
    BulkScoringOptions options,
    Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopes) : ISharePointAccessContext
{
    public async Task<DelegatedGraphAccess> GetGraphAccessAsync(
        SourceCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Phase == SourcePhase.Interactive)
            return new DelegatedGraphAccess(
                "mock-delegated-token:" + context.RecruiterActorId,
                "mock-organizational-tenant");

        if (string.IsNullOrWhiteSpace(context.RequestId))
            throw new InvalidOperationException("Background reads require a request identifier.");

        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .CreateAsyncScope(scopes);
        var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IRequestAuthorizationStore>(scope.ServiceProvider);
        return new DelegatedGraphAccess(
            await store.AcquireGraphTokenAsync(context.RequestId, cancellationToken),
            "mock-organizational-tenant");
    }
}
