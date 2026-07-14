using System.Text.Json;
using MediatR;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Applications.Commands;

public record ReparseScoringRunCommand(
    string ApplicationId,
    string ScoringRunId,
    string? RawJson = null
) : IRequest<ReparseScoringRunResult>;

public record ReparseScoringRunResult(
    ScoringRun ScoringRun,
    bool FallbackParsingActivated,
    bool EligibilityFallbackActivated,
    bool TotalScoreFallbackActivated,
    bool GateDetected,
    string EligibilityPath,
    string Source
);

public class ReparseScoringRunCommandHandler : IRequestHandler<ReparseScoringRunCommand, ReparseScoringRunResult>
{
    private readonly IApplicationRepository _applicationRepo;
    private readonly IJobRepository _jobRepo;

    public ReparseScoringRunCommandHandler(
        IApplicationRepository applicationRepo,
        IJobRepository jobRepo)
    {
        _applicationRepo = applicationRepo;
        _jobRepo = jobRepo;
    }

    public async Task<ReparseScoringRunResult> Handle(ReparseScoringRunCommand request, CancellationToken ct)
    {
        var application = await _applicationRepo.GetByIdAsync(request.ApplicationId, ct)
            ?? throw new InvalidOperationException("Application not found");

        var existingRun = await _applicationRepo.GetScoringRunByIdAsync(request.ScoringRunId, ct)
            ?? throw new InvalidOperationException("Scoring run not found");

        if (!string.Equals(existingRun.ApplicationId, request.ApplicationId, StringComparison.Ordinal))
            throw new InvalidOperationException("Scoring run does not belong to the specified application");

        var source = "request";
        var sourceJson = request.RawJson;
        JsonDocument? sourceDoc = null;

        if (string.IsNullOrWhiteSpace(sourceJson))
        {
            sourceJson = existingRun.RawResponseText;
            source = "stored_raw_response";
        }

        if (string.IsNullOrWhiteSpace(sourceJson))
        {
            sourceJson = existingRun.RawParsedResponseJson;
            source = "stored_raw_parsed_json";
        }

        if (string.IsNullOrWhiteSpace(sourceJson))
        {
            sourceJson = TryGetRawResponse(existingRun.MustHaveEvaluationJson);
            source = "stored_legacy_raw_response";
        }

        if (!string.IsNullOrWhiteSpace(sourceJson))
        {
            var jsonText = ScoreApplicationCommandHandler.ExtractJsonFromResponse(sourceJson);
            sourceDoc = JsonDocument.Parse(jsonText);
        }
        else if (TryBuildGateOnlySourceDoc(existingRun.MustHaveEvaluationJson, out var gateOnlyDoc))
        {
            source = "stored_must_have_json";
            sourceDoc = gateOnlyDoc;
        }
        else
        {
            throw new InvalidOperationException("No raw test score JSON is available to re-parse for this run.");
        }

        var parsedSourceDoc = sourceDoc
            ?? throw new InvalidOperationException("No re-parse source JSON could be loaded for this run.");

        using (parsedSourceDoc)
        {
            var parsed = ScoreApplicationCommandHandler.ParseSingleRunWithDiagnostics(
                parsedSourceDoc.RootElement,
                existingRun.ApplicationId,
                existingRun.PromptVersion,
                existingRun.RunIndex);

            var job = await _jobRepo.GetByIdAsync(application.JobId, ct);
            if (job != null)
            {
                var configVersions = await _jobRepo.GetConfigVersionsAsync(application.JobId, ct);
                var config = configVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
                             ?? configVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                ScoreApplicationCommandHandler.RemapToRubricStatic(parsed.Run, config?.RubricJson);
            }

            if (string.Equals(source, "stored_must_have_json", StringComparison.Ordinal))
            {
                // Gate-only fallback intentionally refreshes eligibility JSON only.
                existingRun.MustHaveEvaluationJson = parsed.Run.MustHaveEvaluationJson;
            }
            else
            {
                existingRun.TotalScore = parsed.Run.TotalScore;
                existingRun.CategoryScoresJson = parsed.Run.CategoryScoresJson;
                existingRun.MustHaveEvaluationJson = parsed.Run.MustHaveEvaluationJson;
                existingRun.EvidenceCitationsJson = parsed.Run.EvidenceCitationsJson;
                existingRun.ImprovementTipsJson = parsed.Run.ImprovementTipsJson;
            }
            await _applicationRepo.UpdateScoringRunAsync(existingRun, ct);

            var diagnostics = parsed.Diagnostics;
            return new ReparseScoringRunResult(
                existingRun,
                diagnostics.FallbackParsingActivated,
                diagnostics.EligibilityFallbackActivated,
                diagnostics.TotalScoreFallbackActivated,
                diagnostics.GateDetected,
                diagnostics.EligibilityPath,
                source);
        }
    }

    private static string? TryGetRawResponse(string? mustHaveEvaluationJson)
    {
        if (string.IsNullOrWhiteSpace(mustHaveEvaluationJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(mustHaveEvaluationJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (doc.RootElement.TryGetProperty("rawResponse", out var raw)
                && raw.ValueKind == JsonValueKind.String)
            {
                var value = raw.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryBuildGateOnlySourceDoc(string? mustHaveEvaluationJson, out JsonDocument? sourceDoc)
    {
        sourceDoc = null;
        if (string.IsNullOrWhiteSpace(mustHaveEvaluationJson))
            return false;

        try
        {
            using var gateDoc = JsonDocument.Parse(mustHaveEvaluationJson);
            if (gateDoc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return false;

            var evidenceOnlyGateJson = BuildEvidenceOnlyGateJson(gateDoc.RootElement);
            if (string.IsNullOrWhiteSpace(evidenceOnlyGateJson))
                return false;

            var wrapped = $"{{\"must_have_requirements\":{evidenceOnlyGateJson}}}";
            sourceDoc = JsonDocument.Parse(wrapped);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? BuildEvidenceOnlyGateJson(JsonElement gateRoot)
    {
        if (gateRoot.ValueKind == JsonValueKind.Array)
        {
            var entries = SimplifyEntries(gateRoot);
            return entries.Count == 0 ? null : JsonSerializer.Serialize(entries);
        }

        if (gateRoot.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetNestedEntriesArray(gateRoot, out var nestedEntries))
        {
            var entries = SimplifyEntries(nestedEntries);
            return entries.Count == 0
                ? null
                : JsonSerializer.Serialize(new
                {
                    details = new { entries }
                });
        }

        return gateRoot.GetRawText();
    }

    private static bool TryGetNestedEntriesArray(JsonElement gateRoot, out JsonElement entries)
    {
        if (gateRoot.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("entries", out var detailEntries)
            && detailEntries.ValueKind == JsonValueKind.Array)
        {
            entries = detailEntries;
            return true;
        }

        foreach (var prop in gateRoot.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                entries = prop.Value;
                return true;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in prop.Value.EnumerateObject())
                {
                    if (nested.Value.ValueKind == JsonValueKind.Array)
                    {
                        entries = nested.Value;
                        return true;
                    }
                }
            }
        }

        entries = default;
        return false;
    }

    private static List<object> SimplifyEntries(JsonElement array)
    {
        var entries = new List<object>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var criterion = FirstString(item, "criterion", "requirement", "item", "name");
            if (string.IsNullOrWhiteSpace(criterion))
                continue;

            var evidence = ExtractEvidence(item);
            entries.Add(new
            {
                criterion,
                evidence
            });
        }

        return entries;
    }

    private static string FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }

        return string.Empty;
    }

    private static string ExtractEvidence(JsonElement obj)
    {
        if (!obj.TryGetProperty("evidence", out var evidence))
            return string.Empty;

        if (evidence.ValueKind == JsonValueKind.String)
            return evidence.GetString()?.Trim() ?? string.Empty;

        if (evidence.ValueKind == JsonValueKind.Array)
        {
            var snippets = evidence
                .EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToList();

            return snippets.Count == 0 ? string.Empty : string.Join("; ", snippets);
        }

        return string.Empty;
    }
}
