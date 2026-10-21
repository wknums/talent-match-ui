using System.Net;
using System.Security.Cryptography;
using System.Text;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public sealed class BulkSourceAdmissionService(
    IBulkSourceAdmissionRepository repository,
    BulkScoringOptions options,
    byte[] hmacKey,
    TimeProvider clock)
{
    public async Task<bool> TryAdmitConnectionStartAsync(
        string productActorId,
        IPAddress sourceAddress,
        int unexpiredPendingConnections,
        CancellationToken cancellationToken = default)
    {
        if (unexpiredPendingConnections >= options.PendingConnectionsPerActor) return false;
        var now = clock.GetUtcNow().UtcDateTime;
        return await repository.TryAdmitConnectionStartAsync(
            Hash("actor", productActorId),
            Hash("ip", Normalize(sourceAddress)),
            now,
            options.ConnectionStartsPerActorPerMinute,
            options.ConnectionStartsPerIpPerMinute,
            cancellationToken);
    }

    public Task<bool> TryAdmitCallbackAsync(
        IPAddress sourceAddress,
        CancellationToken cancellationToken = default) =>
        repository.TryAdmitCallbackAsync(
            Hash("ip", Normalize(sourceAddress)),
            clock.GetUtcNow().UtcDateTime,
            options.CallbacksPerIpPerMinute,
            cancellationToken);

    private string Hash(string kind, string subject)
    {
        if (hmacKey.Length < 32)
            throw new InvalidOperationException("The admission HMAC key must contain at least 256 bits.");
        using var hmac = new HMACSHA256(hmacKey);
        return Convert.ToHexString(hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"bulk-source-admission:v1:{kind}:{subject}"))).ToLowerInvariant();
    }

    private static string Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString().ToLowerInvariant();
}
