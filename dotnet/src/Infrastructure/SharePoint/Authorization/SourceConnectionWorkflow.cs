using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.SourceConnections;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

public sealed class DisabledSourceConnectionWorkflow : ISourceConnectionWorkflow
{
    public Task<int> CountUnexpiredPendingAsync(
        string productActorId,
        DateTime utcNow,
        CancellationToken cancellationToken) =>
        Task.FromResult(0);

    public Task<BeginSourceConnectionResponse> BeginAsync(
        SourceConnectionStart start,
        CancellationToken cancellationToken) =>
        throw BulkApiException.Unavailable();

    public Task<SourceConnectionCallbackResult> CompleteCallbackAsync(
        SourceConnectionCallback callback,
        CancellationToken cancellationToken) =>
        throw BulkApiException.Unavailable();
}

public sealed class SourceConnectionWorkflow(
    IBulkSourceConnectionRepository repository,
    ISharePointSource source,
    IAuthorizationKeyWrapper keyWrapper,
    PendingSourceAuthorizationStore pendingAuthorizations,
    ISourceAuthorizationCodeClient codeClient,
    IClientAssertionProvider assertionProvider,
    BulkScoringOptions options,
    TimeProvider clock) : ISourceConnectionWorkflow
{
    public Task<int> CountUnexpiredPendingAsync(
        string productActorId,
        DateTime utcNow,
        CancellationToken cancellationToken) =>
        repository.CountUnexpiredPendingAsync(productActorId, utcNow, cancellationToken);

    public async Task<BeginSourceConnectionResponse> BeginAsync(
        SourceConnectionStart start,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid().ToString();
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = SourceAuthorizationCryptography.CreateS256Challenge(verifier);
        var canonicalUrlHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(start.FolderUrl.AbsoluteUri)));
        var binding = AuthorizationEnvelope.PendingBinding(
            id,
            start.ProductActorId,
            start.JobId,
            canonicalUrlHash);

        var dataKey = RandomNumberGenerator.GetBytes(32);
        var folderPlaintext = Encoding.UTF8.GetBytes(start.FolderUrl.AbsoluteUri);
        var verifierPlaintext = Encoding.UTF8.GetBytes(verifier);
        byte[] folderCiphertext;
        byte[] verifierCiphertext;
        AuthorizationSecret wrapped;
        try
        {
            folderCiphertext = AuthorizationEnvelope.Seal(
                folderPlaintext,
                dataKey,
                binding + ":folder");
            verifierCiphertext = AuthorizationEnvelope.Seal(
                verifierPlaintext,
                dataKey,
                binding + ":verifier");
            wrapped = await keyWrapper.WrapAsync(dataKey, cancellationToken);
        }
        finally
        {
            AuthorizationEnvelope.Discard(folderPlaintext, verifierPlaintext, dataKey);
        }

        var redirectAfterPath = $"/jobs/{Uri.EscapeDataString(start.JobId)}";
        if (!string.IsNullOrWhiteSpace(start.RetryOfRequestId))
        {
            redirectAfterPath = QueryHelpers.AddQueryString(
                redirectAfterPath,
                "retryBulkRequestId",
                start.RetryOfRequestId);
        }

        var connection = BulkSourceConnection.Begin(
            id,
            start.ProductActorId,
            start.JobId,
            canonicalUrlHash,
            folderCiphertext,
            SourceAuthorizationCryptography.HashState(state),
            verifierCiphertext,
            redirectAfterPath,
            now,
            options.ConnectionIdleTtl,
            options.ConnectionAbsoluteTtl);
        connection.ProtectPendingKey(wrapped.WrappedDataKey, wrapped.KeyVersion);

        var authorizationUrl = BuildAuthorizationUrl(start.CallbackUri, state, challenge);
        if (options.Mode == SharePointMode.Mock)
        {
            var folder = await ResolveFolderAsync(
                connection,
                start.FolderUrl.AbsoluteUri,
                cancellationToken);
            connection.CompleteAuthorization(
                connection.StateHash!,
                folder.TenantId,
                "mock-source-account",
                [1],
                [2],
                "mock-v1",
                now,
                options.ConnectionIdleTtl,
                options.ConnectionAbsoluteTtl);
            VerifyFolder(connection, folder, "Mock organizational account", now);
        }

        await repository.AddWithAuditAsync(
            connection,
            BulkAuditWriter.User(
                BulkAuditEventTypes.SourceConnectionCreated,
                start.ProductActorId,
                start.CorrelationId,
                now,
                "source_connection",
                id,
                new { state = BulkSourceConnectionStatus.Authorizing.ToWire() }),
            cancellationToken);

        return new BeginSourceConnectionResponse(
            id,
            connection.Status.ToWire(),
            authorizationUrl,
            connection.ExpiresAt);
    }

    public async Task<SourceConnectionCallbackResult> CompleteCallbackAsync(
        SourceConnectionCallback callback,
        CancellationToken cancellationToken)
    {
        var stateHash = SourceAuthorizationCryptography.HashState(callback.State);
        var connection = await repository.GetByStateHashAsync(stateHash, cancellationToken)
            ?? throw InvalidOrExpired();
        var binding = new PendingSourceAuthorizationBinding(
            connection.Id,
            connection.ProductActorId,
            connection.JobId,
            connection.CanonicalUrlHash);

        if (!string.IsNullOrWhiteSpace(callback.Error)
            || string.IsNullOrWhiteSpace(callback.Code))
        {
            await pendingAuthorizations.ClearCallbackFailureAsync(
                binding,
                "source_access_denied",
                cancellationToken);
            throw new BulkApiException(400, "source_access_denied", "Microsoft 365 source access was not granted.");
        }

        var (folderUrl, verifier) = await OpenPendingSecretsAsync(connection, binding, cancellationToken);
        try
        {
            var assertion = await assertionProvider.GetClientAssertionAsync(cancellationToken);
            var identity = await codeClient.RedeemAsync(
                new SourceCodeRedemptionRequest(
                    SourceClientId,
                    new Uri(SourceAuthority),
                    QualifiedScopes(),
                    callback.Code,
                    verifier,
                    callback.RedirectUri,
                    assertion),
                cancellationToken);
            SourceCallbackProcessor.ValidateIdentity(identity);

            await pendingAuthorizations.StoreAuthorizedCacheAsync(
                binding,
                stateHash,
                identity.TenantId,
                SourceAuthorizationCryptography.HashAccount(identity.TenantId, identity.AccountId),
                identity.TokenCache,
                cancellationToken);

            var folder = await ResolveFolderAsync(connection, folderUrl, cancellationToken);
            if (!string.Equals(folder.TenantId, identity.TenantId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(folder.TenantId))
                throw new UnsupportedSourceException("The folder belongs to a different source tenant.");
            VerifyFolder(connection, folder, identity.AccountId, clock.GetUtcNow().UtcDateTime);
            await repository.SaveAsync(connection, cancellationToken);

            return new SourceConnectionCallbackResult(new Uri(
                QueryHelpers.AddQueryString(
                    connection.RedirectAfterPath,
                    "sourceConnectionId",
                    connection.Id),
                UriKind.Relative));
        }
        catch (BulkApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (connection.IsPending)
            {
                await pendingAuthorizations.ClearCallbackFailureAsync(
                    binding,
                    "source_connection_invalid_or_expired",
                    cancellationToken);
            }
            throw InvalidOrExpired();
        }
    }

    private Uri BuildAuthorizationUrl(Uri callbackUri, string state, string challenge)
    {
        var endpoint = SourceAuthority.TrimEnd('/') + "/oauth2/v2.0/authorize";
        return new Uri(QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>
        {
            ["client_id"] = SourceClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = callbackUri.AbsoluteUri,
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', QualifiedScopes()),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        }));
    }

    private string[] QualifiedScopes() =>
        options.SourceScopes
            .Select(Microsoft365SourceConnector.QualifyOAuthScope)
            .ToArray();

    private string SourceClientId =>
        !string.IsNullOrWhiteSpace(options.SourceClientId)
            ? options.SourceClientId
            : options.Mode == SharePointMode.Mock
                ? "talentmatch-source-connector"
                : Required(options.SourceClientId);

    private string SourceAuthority =>
        !string.IsNullOrWhiteSpace(options.SourceAuthority)
            ? options.SourceAuthority
            : options.Mode == SharePointMode.Mock
                ? "https://login.microsoftonline.com/organizations"
                : Required(options.SourceAuthority);

    private async Task<(string FolderUrl, string Verifier)> OpenPendingSecretsAsync(
        BulkSourceConnection connection,
        PendingSourceAuthorizationBinding binding,
        CancellationToken cancellationToken)
    {
        if (connection.IsExpired(clock.GetUtcNow().UtcDateTime)
            || connection.PendingFolderUrlEncrypted is null
            || connection.PkceVerifierEncrypted is null
            || connection.WrappedDataKey is null
            || connection.KeyVersion is null)
            throw InvalidOrExpired();

        var dataKey = await keyWrapper.UnwrapAsync(
            connection.WrappedDataKey,
            connection.KeyVersion,
            cancellationToken);
        byte[]? folderPlaintext = null;
        byte[]? verifierPlaintext = null;
        try
        {
            folderPlaintext = AuthorizationEnvelope.Open(
                connection.PendingFolderUrlEncrypted,
                dataKey,
                binding.AssociatedData + ":folder");
            verifierPlaintext = AuthorizationEnvelope.Open(
                connection.PkceVerifierEncrypted,
                dataKey,
                binding.AssociatedData + ":verifier");
            return (
                Encoding.UTF8.GetString(folderPlaintext),
                Encoding.UTF8.GetString(verifierPlaintext));
        }
        finally
        {
            AuthorizationEnvelope.Discard(dataKey, folderPlaintext, verifierPlaintext);
        }
    }

    private async Task<SourceFolder> ResolveFolderAsync(
        BulkSourceConnection connection,
        string folderUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.ResolveFolderAsync(
                folderUrl,
                new SourceCallContext(
                    SourcePhase.Interactive,
                    connection.ProductActorId,
                    ConnectionId: connection.Id),
                cancellationToken);
        }
        catch (SourceAccessDeniedException)
        {
            throw BulkApiException.AccessDenied();
        }
        catch (SourceNotFoundException)
        {
            throw BulkApiException.Invalid("The folder could not be found.");
        }
    }

    private void VerifyFolder(
        BulkSourceConnection connection,
        SourceFolder folder,
        string? sourceAccountDisplay,
        DateTime utcNow) =>
        connection.VerifyFolder(
            folder.Kind,
            folder.DriveId,
            folder.ItemId,
            folder.Name,
            folder.Path,
            folder.WebUrl,
            sourceAccountDisplay,
            utcNow,
            options.ConnectionIdleTtl,
            options.ConnectionAbsoluteTtl);

    private static BulkApiException InvalidOrExpired() =>
        new(400, "source_connection_invalid_or_expired", "The source connection is invalid or has expired.");

    private static string Required(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Microsoft 365 source connector configuration is incomplete.")
            : value;
}
