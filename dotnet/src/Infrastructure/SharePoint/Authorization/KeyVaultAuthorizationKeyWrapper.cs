using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

// Wraps each pending-connection or request-authorization data key with one Key Vault RSA key. The
// managed identity needs only wrap/unwrap rights. Every encrypted-cache write gets a fresh data key;
// the returned key version keeps existing records readable across wrapping-key rotation.
public sealed class KeyVaultAuthorizationKeyWrapper : IAuthorizationKeyWrapper
{
    private static readonly KeyWrapAlgorithm Algorithm = KeyWrapAlgorithm.RsaOaep256;

    private readonly Uri _keyUri;
    private readonly TokenCredential _credential;
    private readonly Func<Uri, CryptographyClient> _clientFor;

    // clientFor lets tests supply a fake vault; production builds a client per key version.
    public KeyVaultAuthorizationKeyWrapper(string keyUri, TokenCredential? credential = null, Func<Uri, CryptographyClient>? clientFor = null)
    {
        _keyUri = new Uri(keyUri, UriKind.Absolute);
        _credential = credential ?? CreateCredential();
        _clientFor = clientFor ?? (uri => new CryptographyClient(uri, _credential));
    }

    public string CurrentKeyVersion => _keyUri.ToString();

    public async Task<AuthorizationSecret> WrapAsync(byte[] dataKey, CancellationToken cancellationToken = default)
    {
        if (dataKey.Length != 32)
            throw new ArgumentException("Authorization envelopes require a 256-bit data key.", nameof(dataKey));
        var result = await _clientFor(_keyUri).WrapKeyAsync(Algorithm, dataKey, cancellationToken);
        return new AuthorizationSecret(result.EncryptedKey, result.KeyId ?? _keyUri.ToString());
    }

    public async Task<byte[]> UnwrapAsync(byte[] wrappedDataKey, string keyVersion, CancellationToken cancellationToken = default)
    {
        if (wrappedDataKey.Length == 0)
            throw new ArgumentException("Wrapped authorization key material is required.", nameof(wrappedDataKey));
        var result = await _clientFor(new Uri(keyVersion, UriKind.Absolute)).UnwrapKeyAsync(Algorithm, wrappedDataKey, cancellationToken);
        return result.Key;
    }

    // AZURE_CLIENT_ID selects a user-assigned managed identity; without it the default chain applies (managed identity, then developer sign-in).
    private static TokenCredential CreateCredential() => new DefaultAzureCredential();
}
