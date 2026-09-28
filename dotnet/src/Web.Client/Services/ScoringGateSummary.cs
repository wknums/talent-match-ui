using System.Text.Json;

namespace TalentMatch.Web.Client.Services;

public sealed record ScoringGateEntryDto(
    string Criterion,
    bool Passed,
    string? Evidence);

public sealed record ParsedScoringGateDto(
    bool Passed,
    IReadOnlyList<string> MissingCriteria,
    IReadOnlyList<ScoringGateEntryDto> Entries);

public sealed record AggregatedGateEntryDto(
    string Criterion,
    bool Passed,
    int PassedVotes,
    int FailedVotes,
    string? Evidence);

public sealed record AggregatedGateSummaryDto(
    bool Passed,
    int PassedVotes,
    int FailedVotes,
    IReadOnlyList<AggregatedGateEntryDto> Entries);

public static class ScoringGateSummary
{
    private static readonly string[] PassedPropertyNames =
        ["passed", "met", "eligible", "satisfied"];
    private static readonly string[] CriterionPropertyNames =
        ["criterion", "requirement", "item", "name", "title"];
    private static readonly string[] EntryStatusPropertyNames =
        ["passed", "met", "satisfied", "eligible", "status", "result", "is_met", "isMet"];
    private static readonly string[] EvidencePropertyNames =
        ["evidence", "supporting_evidence", "citation", "justification", "reason"];
    private static readonly string[] MissingCriteriaPropertyNames =
        ["missing_criteria", "missingCriteria", "missing", "failed_criteria", "unmet_criteria"];

    public static ParsedScoringGateDto? Parse(string? gateJson)
    {
        if (string.IsNullOrWhiteSpace(gateJson) || gateJson == "{}")
            return null;

        try
        {
            using var document = JsonDocument.Parse(gateJson);
            var root = document.RootElement;
            var missingCriteria = ReadMissingCriteria(root);
            var passedHint = root.ValueKind == JsonValueKind.Object
                ? TryReadFlexibleBoolean(root, PassedPropertyNames)
                : null;
            var entries = ReadEntries(root, missingCriteria, passedHint);

            if (!passedHint.HasValue && entries.Count == 0)
                return null;

            var passed = passedHint ?? entries.All(entry => entry.Passed);
            return new ParsedScoringGateDto(passed, missingCriteria, entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AggregatedGateSummaryDto? Build(
        IEnumerable<string?> gateJsonValues,
        IEnumerable<string> configuredCriteria)
    {
        var overallPassed = 0;
        var overallFailed = 0;
        var votes = new Dictionary<string, EntryVotes>(StringComparer.OrdinalIgnoreCase);
        var configured = configuredCriteria
            .Where(criterion => !string.IsNullOrWhiteSpace(criterion))
            .Select(criterion => criterion.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var gateJson in gateJsonValues)
        {
            var gate = Parse(gateJson);
            if (gate is null)
                continue;

            if (gate.Passed)
                overallPassed++;
            else
                overallFailed++;

            foreach (var entry in gate.Entries)
                AddVote(votes, entry.Criterion, entry.Passed, entry.Evidence);

            foreach (var criterion in configured)
            {
                if (gate.Entries.Any(entry => CriteriaMatch(entry.Criterion, criterion)))
                    continue;

                var missing = gate.MissingCriteria.Any(item => CriteriaMatch(item, criterion));
                if (missing)
                    AddVote(votes, criterion, false, null);
                else if (gate.Passed || gate.MissingCriteria.Count > 0)
                    AddVote(votes, criterion, true, null);
            }
        }

        if (overallPassed + overallFailed == 0)
            return null;

        var orderedCriteria = configured
            .Concat(votes.Keys.Where(key => !configured.Contains(key, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var entries = orderedCriteria
            .Where(votes.ContainsKey)
            .Select(criterion =>
            {
                var vote = votes[criterion];
                return new AggregatedGateEntryDto(
                    criterion,
                    vote.PassedVotes >= vote.FailedVotes && vote.PassedVotes > 0,
                    vote.PassedVotes,
                    vote.FailedVotes,
                    vote.FailureEvidence ?? vote.SuccessEvidence);
            })
            .ToList();

        return new AggregatedGateSummaryDto(
            overallPassed >= overallFailed,
            overallPassed,
            overallFailed,
            entries);
    }

    private static List<ScoringGateEntryDto> ReadEntries(
        JsonElement root,
        IReadOnlyList<string> missingCriteria,
        bool? passedHint)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return ParseEntryArray(root, missingCriteria, passedHint);

        if (root.ValueKind != JsonValueKind.Object)
            return [];

        if (TryGetProperty(root, "entries", out var topLevelEntries)
            && topLevelEntries.ValueKind == JsonValueKind.Array)
        {
            var parsed = ParseEntryArray(topLevelEntries, missingCriteria, passedHint);
            if (parsed.Count > 0)
                return parsed;
        }

        if (TryGetProperty(root, "details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(details, "entries", out var detailEntries)
                && detailEntries.ValueKind == JsonValueKind.Array)
            {
                var parsed = ParseEntryArray(detailEntries, missingCriteria, passedHint);
                if (parsed.Count > 0)
                    return parsed;
            }

            var flatEntries = ParseFlatEntries(details);
            if (flatEntries.Count > 0)
                return flatEntries;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!IsObjectArray(property.Value))
                continue;

            var parsed = ParseEntryArray(property.Value, missingCriteria, passedHint);
            if (parsed.Count > 0)
                return parsed;
        }

        return [];
    }

    private static List<ScoringGateEntryDto> ParseEntryArray(
        JsonElement array,
        IReadOnlyList<string> missingCriteria,
        bool? passedHint)
    {
        var entries = new List<ScoringGateEntryDto>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryReadString(item, CriterionPropertyNames, out var criterion))
            {
                continue;
            }

            var evidence = TryReadEvidence(item);
            var passed = TryReadFlexibleBoolean(item, EntryStatusPropertyNames)
                ?? InferEntryStatus(criterion, evidence, missingCriteria, passedHint);
            entries.Add(new ScoringGateEntryDto(criterion, passed, evidence));
        }

        return entries;
    }

    private static List<ScoringGateEntryDto> ParseFlatEntries(JsonElement details)
    {
        var entries = new List<ScoringGateEntryDto>();

        foreach (var property in details.EnumerateObject())
        {
            if (property.NameEquals("source")
                || property.NameEquals("entries")
                || property.NameEquals("recommendation"))
            {
                continue;
            }

            if (TryParseFlexibleBoolean(property.Value, out var passed))
                entries.Add(new ScoringGateEntryDto(property.Name, passed, null));
        }

        return entries;
    }

    private static bool InferEntryStatus(
        string criterion,
        string? evidence,
        IReadOnlyList<string> missingCriteria,
        bool? passedHint)
    {
        if (missingCriteria.Any(missing => CriteriaMatch(missing, criterion)))
            return false;

        if (!string.IsNullOrWhiteSpace(evidence))
            return !LooksLikeNegativeEvidence(evidence);

        return passedHint ?? false;
    }

    private static List<string> ReadMissingCriteria(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var propertyName in MissingCriteriaPropertyNames)
        {
            if (!TryGetProperty(root, propertyName, out var missing)
                || missing.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return missing.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    private static string? TryReadEvidence(JsonElement item)
    {
        foreach (var propertyName in EvidencePropertyNames)
        {
            if (!TryGetProperty(item, propertyName, out var evidence))
                continue;

            if (evidence.ValueKind == JsonValueKind.String)
            {
                var text = evidence.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            if (evidence.ValueKind == JsonValueKind.Array)
            {
                var text = string.Join(
                    "; ",
                    evidence.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()?.Trim())
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }

        return null;
    }

    private static bool? TryReadFlexibleBoolean(JsonElement element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property)
                && TryParseFlexibleBoolean(property, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryParseFlexibleBoolean(JsonElement value, out bool parsed)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            parsed = true;
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            parsed = false;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            parsed = number != 0;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var normalized = Normalize(value.GetString() ?? string.Empty);
            if (ContainsAny(
                    normalized,
                    "not met",
                    "does not meet",
                    "failed",
                    "fail",
                    "false",
                    "no",
                    "ineligible",
                    "missing",
                    "unmet",
                    "unsatisfied"))
            {
                parsed = false;
                return true;
            }

            if (ContainsAny(
                    normalized,
                    "met",
                    "meets",
                    "passed",
                    "pass",
                    "true",
                    "yes",
                    "eligible",
                    "satisfied",
                    "success"))
            {
                parsed = true;
                return true;
            }

            if (normalized == "1")
            {
                parsed = true;
                return true;
            }

            if (normalized == "0")
            {
                parsed = false;
                return true;
            }
        }

        parsed = false;
        return false;
    }

    private static bool LooksLikeNegativeEvidence(string evidence)
    {
        var normalized = Normalize(evidence);
        return ContainsAny(
            normalized,
            "none found",
            "not found",
            "no evidence",
            "no proof",
            "not provided",
            "insufficient evidence",
            "unable to verify",
            "cannot verify",
            "missing evidence",
            "no supporting evidence",
            "unknown",
            "not met",
            "does not meet",
            "did not meet",
            "could not be verified",
            "without evidence");
    }

    private static void AddVote(
        Dictionary<string, EntryVotes> votes,
        string criterion,
        bool passed,
        string? evidence)
    {
        var key = votes.Keys.FirstOrDefault(existing => CriteriaMatch(existing, criterion))
            ?? criterion.Trim();
        if (!votes.TryGetValue(key, out var value))
            votes[key] = value = new EntryVotes();
        if (passed)
        {
            value.PassedVotes++;
            if (!string.IsNullOrWhiteSpace(evidence))
                value.SuccessEvidence ??= evidence.Trim();
        }
        else
        {
            value.FailedVotes++;
            if (!string.IsNullOrWhiteSpace(evidence))
                value.FailureEvidence ??= evidence.Trim();
        }
    }

    private static bool CriteriaMatch(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft)
            || string.IsNullOrWhiteSpace(normalizedRight))
        {
            return false;
        }

        return normalizedLeft == normalizedRight
            || normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
            || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        var characters = value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();
        return string.Join(
            ' ',
            new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.Ordinal));

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadString(
        JsonElement element,
        IEnumerable<string> names,
        out string value)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var property)
                || property.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.GetString()))
            {
                continue;
            }

            value = property.GetString()!.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsObjectArray(JsonElement value)
        => value.ValueKind == JsonValueKind.Array
           && value.GetArrayLength() > 0
           && value[0].ValueKind == JsonValueKind.Object;

    private sealed class EntryVotes
    {
        public int PassedVotes { get; set; }
        public int FailedVotes { get; set; }
        public string? SuccessEvidence { get; set; }
        public string? FailureEvidence { get; set; }
    }
}
