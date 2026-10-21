using System.Security.Cryptography;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

// Local-development key wrapper (BULK_AUTH_DEV_KEY_PATH). It is real AES-GCM, but the key lives in a
// file or in memory, so options validation rejects it in Production.
public sealed class LocalAuthorizationKeyWrapper : IAuthorizationKeyWrapper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public string CurrentKeyVersion { get; }

    public LocalAuthorizationKeyWrapper(string? keyPath = null)
    {
        if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
        {
            _key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            if (!string.IsNullOrWhiteSpace(keyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
                File.WriteAllText(keyPath, Convert.ToBase64String(_key));
            }
        }

        if (_key.Length != 32) throw new InvalidOperationException("The dev wrapping key must be 32 bytes.");
        CurrentKeyVersion = "local-" + Convert.ToHexString(SHA256.HashData(_key))[..8].ToLowerInvariant();
    }

    public Task<AuthorizationSecret> WrapAsync(byte[] dataKey, CancellationToken cancellationToken = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[dataKey.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, dataKey, cipher, tag);

        var wrapped = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(wrapped, 0);
        tag.CopyTo(wrapped, NonceSize);
        cipher.CopyTo(wrapped, NonceSize + TagSize);
        return Task.FromResult(new AuthorizationSecret(wrapped, CurrentKeyVersion));
    }

    public Task<byte[]> UnwrapAsync(byte[] wrappedDataKey, string keyVersion, CancellationToken cancellationToken = default)
    {
        if (keyVersion != CurrentKeyVersion)
            throw new CryptographicException($"Unknown dev key version '{keyVersion}'.");

        var nonce = wrappedDataKey.AsSpan(0, NonceSize);
        var tag = wrappedDataKey.AsSpan(NonceSize, TagSize);
        var cipher = wrappedDataKey.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Task.FromResult(plain);
    }
}
