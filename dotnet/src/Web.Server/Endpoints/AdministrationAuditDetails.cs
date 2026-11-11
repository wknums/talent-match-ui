namespace TalentMatch.Web.Server.Endpoints;

internal static class AdministrationAuditDetails
{
    public static Dictionary<string, object?> WithCurrent(HttpContext http, Dictionary<string, object?> details)
    {
        if (http.Items["TalentMatch.AdministrationAudit"] is IReadOnlyDictionary<string, object?> current)
            foreach (var (key, value) in current) details[key] = value;
        return details;
    }
}
