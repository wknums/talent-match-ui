using System.Security.Cryptography;
using System.Text;

namespace TalentMatch.Application.Common.Services;

public static class ContentFingerprint
{
    public static string Compute(ReadOnlySpan<byte> originalBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(originalBytes));

    public static string ComputeFromBase64(string contentBase64) =>
        Compute(Convert.FromBase64String(contentBase64));

    public static string LegacyBase64(ReadOnlySpan<byte> originalBytes) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToBase64String(originalBytes))));

    public static string LegacyOfBase64(string contentBase64) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contentBase64)));

    public static IReadOnlyList<string> LookupCandidates(ReadOnlySpan<byte> originalBytes) =>
        WithHistoricalCasing(Compute(originalBytes), LegacyBase64(originalBytes));

    public static IReadOnlyList<string> LookupCandidatesFromBase64(string contentBase64) =>
        WithHistoricalCasing(ComputeFromBase64(contentBase64), LegacyOfBase64(contentBase64));

    public static string? NormalizeStored(string? stored)
    {
        if (stored is null || stored.Length != 64 || stored.Any(character => !Uri.IsHexDigit(character)))
            return null;

        return stored.ToLowerInvariant();
    }

    public static string Raw(ReadOnlySpan<byte> originalBytes) => Compute(originalBytes);

    public static IReadOnlyList<string> Candidates(ReadOnlySpan<byte> originalBytes) =>
        [Compute(originalBytes), LegacyBase64(originalBytes)];

    private static IReadOnlyList<string> WithHistoricalCasing(string canonical, string legacy)
    {
        var candidates = new[]
        {
            canonical,
            canonical.ToUpperInvariant(),
            legacy,
            legacy.ToUpperInvariant(),
        };
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }
}
