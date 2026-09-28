using TalentMatch.Application.Uploads.Models;

namespace TalentMatch.Application.Uploads;

public static class UploadValidators
{
    public static readonly IReadOnlySet<string> SupportedMimeTypes = new HashSet<string>(
        [
            "application/pdf",
            "text/markdown",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "text/plain",
            "image/jpeg",
            "image/png",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string? NormalizeMimeType(string fileName, string? suppliedMimeType)
    {
        if (!string.IsNullOrWhiteSpace(suppliedMimeType)
            && SupportedMimeTypes.Contains(suppliedMimeType.Trim()))
            return suppliedMimeType.Trim().ToLowerInvariant();

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".txt" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => null,
        };
    }

    public static IReadOnlyList<string> ValidateItem(
        CreateUploadItemRequest item,
        long maxIndividualFileBytes)
    {
        var errors = new List<string>();
        if (item.OccurrenceKey == Guid.Empty)
            errors.Add("A non-empty occurrence key is required.");
        if (item.Ordinal < 0)
            errors.Add("Ordinal must be zero or greater.");
        if (string.IsNullOrWhiteSpace(item.FileName) || item.FileName.Length > 500)
            errors.Add("File name must contain between 1 and 500 characters.");
        if (NormalizeMimeType(item.FileName, item.MimeType) is null)
            errors.Add("Only PDF, Markdown, DOCX, TXT, JPG, and PNG files are supported.");
        if (item.RawSizeBytes < 0)
            errors.Add("Raw file size cannot be negative.");
        else if (item.RawSizeBytes > maxIndividualFileBytes)
            errors.Add($"Raw file size exceeds the {maxIndividualFileBytes} byte limit.");
        return errors;
    }
}
