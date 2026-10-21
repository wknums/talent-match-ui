using TalentMatch.Domain.Entities.BulkScoringRequests;
using System.Text;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public sealed record ClassifiedLink(BulkSourceKind Kind, Uri Uri);

// Classifies a pasted folder link by host before any outbound call (anti-SSRF, FR-002F). The server
// never fetches the link itself; the classified link is only turned into a share token for Graph.
public sealed class SourceLinkClassifier
{
    public bool TryClassify(string? url, out ClassifiedLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

        // Structural validation happens locally. The pasted URL is never fetched directly.
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || url.Any(char.IsControl))
            return false;
        try
        {
            if (Uri.UnescapeDataString(uri.PathAndQuery).Any(char.IsControl)) return false;
        }
        catch (UriFormatException)
        {
            return false;
        }

        var host = uri.IdnHost;
        if (!CommercialCloudClassifier.TryClassifyOrganizationalHost(host, out var kind)) return false;
        link = new(kind, SourceFolderUrl.Normalize(uri));
        return true;
    }

    // Throws UnsupportedSourceException with the message shown to the recruiter.
    public ClassifiedLink Require(string? url) =>
        TryClassify(url, out var link)
            ? link!
            : throw new UnsupportedSourceException(
                "Only work or school SharePoint and OneDrive folder links are supported.");

    public static string ToSharingToken(Uri uri)
    {
        var bytes = Encoding.UTF8.GetBytes(uri.AbsoluteUri);
        return "u!" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('/', '_').Replace('+', '-');
    }
}
