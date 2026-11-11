using System.Text.Json;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public static class BulkSourceDisclosurePolicy
{
    private static readonly string[] ProhibitedFactNames =
    [
        "url",
        "foldername",
        "itemname",
        "filename",
        "path",
        "email",
        "account",
        "authorizationcode",
        "token",
        "verifier",
        "cache",
        "key",
        "fingerprint",
        "contentbase64",
        "providerpayload",
        "resourceid",
        "secret",
    ];

    public static void ValidateSafeFacts(string safeFactsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(safeFactsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Audit safe facts must be valid JSON.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("factVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var value)
                || value <= 0)
                throw new InvalidOperationException("Audit safe facts require a positive factVersion.");

            ValidateElement(document.RootElement);
        }
    }

    public static string SafeProblemDetail(int status, string code) => code switch
    {
        "source_unsupported_account" => "Only organizational Microsoft 365 accounts are supported.",
        "source_unsupported_cloud" => "Only Microsoft 365 commercial-cloud accounts are supported.",
        "source_folder_invalid_or_unavailable" or "unsupported_source" =>
            "The source folder is invalid, unavailable, or unsupported.",
        "source_connection_invalid_or_expired" => "The source connection is invalid or has expired.",
        "source_connection_limit_reached" => "Too many source connections are pending.",
        "source_connection_rate_limited" or "source_callback_rate_limited" =>
            "Too many requests were received. Try again later.",
        "no_retryable_bulk_files" =>
            "No files from this request can be retried here. Files that failed while being scored are retried from the Failure Queue; otherwise start a new bulk scoring request.",
        "sharepoint_access_denied" or "source_access_denied" => "Access to the source was not granted.",
        "forbidden" => "Access to this resource is not permitted.",
        "not_found" => "The requested resource was not found.",
        "feature_unavailable" or "source_connector_unavailable" =>
            "This feature is not available in this environment.",
        "validation_failed" => "The request is invalid.",
        _ when status >= 500 => "The request could not be completed.",
        _ => "The request could not be completed.",
    };

    private static void ValidateElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant();
                if (ProhibitedFactNames.Any(normalized.Contains))
                    throw new InvalidOperationException(
                        $"Audit safe facts contain prohibited property '{property.Name}'.");
                ValidateElement(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateElement(item);
        }
    }
}
