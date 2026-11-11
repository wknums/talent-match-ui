namespace TalentMatch.Web.Client.Services;

public static class ScoringEvidenceEvaluation
{
    private static readonly HashSet<string> PassedStatuses =
        new(["pass", "passed", "met", "evidenced", "satisfied"], StringComparer.Ordinal);
    private static readonly HashSet<string> FailedStatuses =
        new(["fail", "failed", "not evidenced", "not met", "unmet", "unsatisfied"], StringComparer.Ordinal);

    public static bool? Parse(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
            return null;

        var segments = evidence
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize);

        foreach (var segment in segments)
        {
            if (PassedStatuses.Contains(segment))
                return true;
            if (FailedStatuses.Contains(segment))
                return false;
        }

        return null;
    }

    private static string Normalize(string value)
    {
        var characters = value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();
        return string.Join(
            ' ',
            new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
