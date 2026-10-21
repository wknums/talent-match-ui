using System.Security.Cryptography;
using System.Text;

namespace TalentMatch.Application.BulkScoringRequests.Services;

// RFC 4122 version 5 (name-based, SHA-1) identifiers. A retried create hits the same primary key, so
// re-creating an application or document after a crash is a no-op rather than a duplicate (R8).
public static class DeterministicIds
{
    private static readonly Guid Namespace = new("3f6a2d3e-6f0b-5c1e-9a55-8d2f1c7b4a10");

    public static string Application(string itemId) => Create("application:" + itemId);

    public static string Document(string itemId) => Create("document:" + itemId);

    public static string Create(string name) => Create(Namespace, name);

    // Public so the algorithm can be checked against published RFC 4122 test vectors.
    public static string Create(Guid namespaceId, string name)
    {
        var ns = namespaceId.ToByteArray();
        SwapByteOrder(ns);
        var hash = SHA1.HashData([.. ns, .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        SwapByteOrder(bytes);
        return new Guid(bytes).ToString();
    }

    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
