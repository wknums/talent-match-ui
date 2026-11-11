namespace TalentMatch.Application.BulkScoringRequests;

public static class SourceFolderUrl
{
    public static Uri Normalize(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("A source folder URL must be a safe HTTPS URL.", nameof(uri));
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0 || !string.Equals(Uri.UnescapeDataString(part[..separator]), "id", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = Uri.UnescapeDataString(part[(separator + 1)..]);
            var isOneDrivePersonalPath = uri.IdnHost.Contains("-my.", StringComparison.OrdinalIgnoreCase)
                && path.StartsWith("/personal/", StringComparison.OrdinalIgnoreCase);
            var isSharePointLibraryNavigation = uri.AbsolutePath.EndsWith(
                    "/Forms/AllItems.aspx",
                    StringComparison.OrdinalIgnoreCase)
                && path.StartsWith("/", StringComparison.Ordinal);
            if ((!isOneDrivePersonalPath && !isSharePointLibraryNavigation)
                || path.StartsWith("//", StringComparison.Ordinal)
                || path.Contains('\\')
                || path.Any(char.IsControl)
                || path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or ".."))
                continue;

            return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri;
        }

        return uri;
    }

    public static string Normalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var normalized = Normalize(uri);
        return ReferenceEquals(normalized, uri) ? url : normalized.AbsoluteUri;
    }
}
