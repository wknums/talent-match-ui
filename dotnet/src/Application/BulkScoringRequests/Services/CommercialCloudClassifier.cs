using System.Text.RegularExpressions;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public static partial class CommercialCloudClassifier
{
    private const string CommercialSuffix = ".sharepoint.com";

    public static bool TryClassifyOrganizationalHost(string host, out BulkSourceKind kind)
    {
        kind = default;
        if (host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase)
            || host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase)
            || !host.EndsWith(CommercialSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var tenantLabel = host[..^CommercialSuffix.Length];
        if (!TenantLabel().IsMatch(tenantLabel)) return false;
        kind = tenantLabel.EndsWith("-my", StringComparison.OrdinalIgnoreCase)
            ? BulkSourceKind.OneDriveBusiness
            : BulkSourceKind.SharePoint;
        return true;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TenantLabel();
}
