using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.SharePoint;

/// <summary>
/// Delegated-only Graph token boundary. Interactive token establishment belongs to the separate
/// authorization-code connector; workers may use only the request-bound encrypted cache.
/// </summary>
public sealed class SharePointAccessContext : ISharePointAccessContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BulkScoringOptions _options;

    public SharePointAccessContext(
        BulkScoringOptions options,
        IServiceScopeFactory scopes,
        Action<TokenCredentialOptions>? configureCredentials = null)
    {
        _options = options;
        _ = configureCredentials;
        _scopes = scopes;
    }

    public async Task<DelegatedGraphAccess> GetGraphAccessAsync(
        SourceCallContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (context.Phase == SourcePhase.Interactive
            && !string.IsNullOrWhiteSpace(context.ConnectionId))
        {
            return await AcquireInteractiveTokenAsync(scope.ServiceProvider, context, cancellationToken);
        }
        if (context.Phase != SourcePhase.Background || string.IsNullOrWhiteSpace(context.RequestId))
            throw new AuthorizationRequiredException(
                "Complete the separate Microsoft 365 source connection before reading folder content.");

        var store = scope.ServiceProvider.GetService<IRequestAuthorizationStore>()
            ?? throw new SourceAuthorizationLapsedException("authorization_store_unavailable");
        var authorization = await scope.ServiceProvider
            .GetRequiredService<IBulkRequestAuthorizationRepository>()
            .GetAsync(context.RequestId, cancellationToken)
            ?? throw new SourceAuthorizationLapsedException("authorization_store_unavailable");
        var token = await store.AcquireGraphTokenAsync(context.RequestId, cancellationToken);
        return new DelegatedGraphAccess(token, authorization.SourceTenantId);
    }

    private async Task<DelegatedGraphAccess> AcquireInteractiveTokenAsync(
        IServiceProvider services,
        SourceCallContext context,
        CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IBulkSourceConnectionRepository>();
        var connection = await repository.GetOwnedAsync(
            context.ConnectionId!,
            context.RecruiterActorId,
            cancellationToken)
            ?? throw new AuthorizationRequiredException("The source connection is unavailable.");
        var store = services.GetRequiredService<PendingSourceAuthorizationStore>();
        var binding = new PendingSourceAuthorizationBinding(
            connection.Id,
            connection.ProductActorId,
            connection.JobId,
            connection.CanonicalUrlHash);
        var cache = await store.OpenAsync(binding, cancellationToken);
        try
        {
            var application = ConfidentialClientApplicationBuilder
                .Create(_options.SourceClientId)
                .WithAuthority($"https://login.microsoftonline.com/{cache.SourceTenantId}")
                .WithClientAssertion(
                    (AssertionRequestOptions request) =>
                        ManagedIdentityClientAssertion.GetAsync(request.CancellationToken))
                .Build();
            application.UserTokenCache.SetBeforeAccess(args =>
                args.TokenCache.DeserializeMsalV3(cache.TokenCache, shouldClearExistingCache: true));

            var account = (await application.GetAccountsAsync()).FirstOrDefault(candidate =>
                candidate.HomeAccountId?.TenantId.Equals(
                    cache.SourceTenantId,
                    StringComparison.OrdinalIgnoreCase) == true
                && SourceAuthorizationCryptography.HashAccount(
                    cache.SourceTenantId,
                    candidate.HomeAccountId.Identifier)
                    .Equals(cache.SourceAccountBindingHash, StringComparison.Ordinal));
            if (account is null)
                throw new AuthorizationRequiredException("The source authorization binding is unavailable.");

            var scopes = _options.SourceScopes
                .Where(scope => scope is not ("openid" or "profile" or "offline_access"))
                .Select(Microsoft365SourceConnector.QualifyOAuthScope)
                .ToArray();
            var result = await application.AcquireTokenSilent(scopes, account)
                .ExecuteAsync(cancellationToken);
            return new DelegatedGraphAccess(result.AccessToken, cache.SourceTenantId);
        }
        catch (MsalUiRequiredException)
        {
            throw new AuthorizationRequiredException(
                "The Microsoft 365 source authorization must be renewed.");
        }
        finally
        {
            AuthorizationEnvelope.Discard(cache.TokenCache);
        }
    }
}
