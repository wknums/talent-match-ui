using System.Security.Cryptography;

namespace TalentMatch.Infrastructure.SharePoint.Authorization;

// Envelope encryption for a pending connection or accepted request. Callers supply the complete,
// non-secret associated-data binding so ciphertext cannot move between records, actors, jobs,
// source URLs, or request IDs.
public static class AuthorizationEnvelope
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static (byte[] Payload, byte[] DataKey) Seal(byte[] plaintext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        return (Seal(plaintext, dataKey, associatedData), dataKey);
    }

    public static byte[] Seal(byte[] plaintext, byte[] dataKey, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(dataKey);
        if (dataKey.Length != 32)
            throw new ArgumentException("Authorization data keys must be 256 bits.", nameof(dataKey));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(dataKey, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Binding(associatedData));

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);
        return payload;
    }

    public static byte[] Open(byte[] payload, byte[] dataKey, string associatedData)
    {
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("Authorization payload is malformed.");

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[cipher.Length];
        using var aes = new AesGcm(dataKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, plaintext, Binding(associatedData));
        return plaintext;
    }

    public static void Discard(params byte[]?[] material)
    {
        foreach (var value in material)
        {
            if (value is { Length: > 0 })
                CryptographicOperations.ZeroMemory(value);
        }
    }

    public static string PendingBinding(
        string connectionId,
        string productActorId,
        string jobId,
        string canonicalUrlHash) =>
        $"bulk-source-connection:{Required(connectionId)}:{Required(productActorId)}:{Required(jobId)}:{Required(canonicalUrlHash)}";

    public static string RequestBinding(string requestId) =>
        $"bulk-source-request:{Required(requestId)}";

    private static byte[] Binding(string associatedData)
    {
        associatedData = Required(associatedData);
        // Compatibility is read-only for the pre-v3 request envelope. New callers use one of the
        // two explicit helpers above; no connection/request cache is translated between bindings.
        if (!associatedData.StartsWith("bulk-source-connection:", StringComparison.Ordinal)
            && !associatedData.StartsWith("bulk-source-request:", StringComparison.Ordinal)
            && !associatedData.StartsWith("bulk-authorization:", StringComparison.Ordinal))
        {
            associatedData = "bulk-authorization:" + associatedData;
        }
        return System.Text.Encoding.UTF8.GetBytes(associatedData);
    }

    private static string Required(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Authorization associated-data components are required.")
            : value;
}
