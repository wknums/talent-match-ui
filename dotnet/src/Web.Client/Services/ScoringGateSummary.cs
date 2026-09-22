using System.Text.Json;

namespace TalentMatch.Web.Client.Services;

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
            if (!TryParseGate(gateJson, out var gate))
                continue;

            if (gate.Passed)
                overallPassed++;
            else
                overallFailed++;

            var explicitEntries = ReadEntries(gate.Root);
            foreach (var entry in explicitEntries)
                AddVote(votes, entry.Criterion, entry.Passed, entry.Evidence);

            foreach (var criterion in configured)
            {
                if (explicitEntries.Any(entry => CriteriaMatch(entry.Criterion, criterion)))
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

    private static bool TryParseGate(string? json, out ParsedGate gate)
    {
        gate = default;
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement.Clone();
            if (!TryReadBoolean(root, "passed", out var passed))
                return false;
            gate = new ParsedGate(root, passed, ReadMissingCriteria(root));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<ParsedEntry> ReadEntries(JsonElement root)
    {
        var entries = new List<ParsedEntry>();
        if (!TryGetProperty(root, "details", out var details)
            || details.ValueKind != JsonValueKind.Object)
            return entries;

        if (TryGetProperty(details, "entries", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryReadString(item, "criterion", out var criterion)
                    || !TryReadBoolean(item, "passed", out var passed))
                    continue;
                TryReadString(item, "evidence", out var evidence);
                entries.Add(new ParsedEntry(criterion, passed, evidence));
            }
            return entries;
        }

        foreach (var property in details.EnumerateObject())
        {
            if (property.NameEquals("source") || property.NameEquals("entries"))
                continue;
            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                entries.Add(new ParsedEntry(
                    property.Name,
                    property.Value.ValueKind == JsonValueKind.True,
                    null));
        }
        return entries;
    }

    private static List<string> ReadMissingCriteria(JsonElement root)
    {
        if (!TryGetProperty(root, "missing_criteria", out var missing)
            || missing.ValueKind != JsonValueKind.Array)
            return [];
        return missing.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToList();
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

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryReadBoolean(
        JsonElement element,
        string name,
        out bool value)
    {
        value = false;
        if (!TryGetProperty(element, name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        return property.ValueKind == JsonValueKind.False;
    }

    private static bool TryReadString(
        JsonElement element,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
            return false;
        value = property.GetString()!.Trim();
        return true;
    }

    private sealed class EntryVotes
    {
        public int PassedVotes { get; set; }
        public int FailedVotes { get; set; }
        public string? SuccessEvidence { get; set; }
        public string? FailureEvidence { get; set; }
    }

    private readonly record struct ParsedEntry(
        string Criterion,
        bool Passed,
        string? Evidence);

    private readonly record struct ParsedGate(
        JsonElement Root,
        bool Passed,
        List<string> MissingCriteria);
}
