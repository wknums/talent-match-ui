using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Uploads;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public sealed record EligibilityResult(
    bool Included,
    BulkReasonCode? Exclusion,
    string? MimeType,
    // True when the file would be excluded if rasterization is enabled; TXT is exempt and always false.
    bool ExcludedWhenRasterized);

// One rulebook for discovery, confirm, and retry: type allowlist, size, emptiness, and whether the
// file can be converted (or is exempt) when rasterizing.
public sealed class EligibilityEvaluator(ISharePointSource source)
{
    private static readonly string[] ConvertedBySourceExtensions = [".docx", ".md", ".markdown"];

    public EligibilityResult Evaluate(SourceFile file, long perFileLimitBytes, bool rasterize)
    {
        if (file.IsPackageOrRemote)
            return new(false, BulkReasonCode.UnsupportedType, null, false);

        // The extension is authoritative; the source's reported media type is not trusted.
        var mime = UploadValidators.NormalizeMimeType(file.Name, null);
        if (mime is null)
            return new(false, BulkReasonCode.UnsupportedType, null, false);

        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        var excludedWhenRasterized = ConvertedBySourceExtensions.Contains(extension) && !source.CanConvertToPdf(extension);

        if (file.SizeBytes <= 0)
            return new(false, BulkReasonCode.EmptyFile, mime, excludedWhenRasterized);
        if (file.SizeBytes > perFileLimitBytes)
            return new(false, BulkReasonCode.FileTooLarge, mime, excludedWhenRasterized);
        if (rasterize && excludedWhenRasterized)
            return new(false, BulkReasonCode.NotConvertible, mime, true);

        return new(true, null, mime, excludedWhenRasterized);
    }

    // At import time the content must match its extension (defense against renamed or hostile files).
    public static bool ContentMatchesType(string mimeType, ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty) return false;
        return mimeType switch
        {
            "application/pdf" => content.StartsWith("%PDF"u8),
            "image/png" => content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            "image/jpeg" => content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => content.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
            "text/plain" or "text/markdown" => !content.Contains((byte)0),
            _ => false,
        };
    }
}
