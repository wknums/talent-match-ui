using System.Security.Cryptography;
using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FluentAssertions;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.Tests.SharePoint;

public sealed class KeyVaultAuthorizationKeyWrapperTests
{
    private const string KeyUri = "https://vault.example.net/keys/bulk-auth";

    [Fact]
    public async Task Wrapping_records_the_exact_key_version_and_unwrapping_goes_to_that_version()
    {
        using var rsa = RSA.Create(2048);
        var key = new JsonWebKey(rsa, includePrivateParameters: true, keyOps: [KeyOperation.WrapKey, KeyOperation.UnwrapKey]) { Id = KeyUri + "/v7" };
        var calls = new List<Uri>();
        var wrapper = new KeyVaultAuthorizationKeyWrapper(KeyUri, new NoCredential(), uri =>
        {
            calls.Add(uri);
            return new CryptographyClient(key);
        });
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = await wrapper.WrapAsync(dataKey);

        wrapped.KeyVersion.Should().Be(KeyUri + "/v7", "the vault reports which version wrapped the key");
        wrapped.WrappedDataKey.Should().NotEqual(dataKey).And.HaveCount(256, "an RSA-2048 OAEP ciphertext");
        (await wrapper.UnwrapAsync(wrapped.WrappedDataKey, wrapped.KeyVersion)).Should().Equal(dataKey);
        calls[0].ToString().Should().Be(KeyUri, "wrapping uses the configured key, whatever its current version is");
        calls[1].ToString().Should().Be(KeyUri + "/v7", "unwrapping uses the version recorded with the data");
        wrapper.CurrentKeyVersion.Should().Be(KeyUri);
    }

    private sealed class NoCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
