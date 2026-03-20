using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Scoring.Commands;

public record ScoreApplicationCommand(
    string ApplicationId,
    string JobId,
    int RunCount,
    string PromptVersionId
) : IRequest<ScoreApplicationResult>;

public record ScoreApplicationResult(
    IReadOnlyList<ScoringRun> Runs,
    EngineAggregatedResult? Aggregated
);

public record EngineAggregatedResult(
    double FinalScore,
    double Variance,
    double Confidence,
    string FinalDecision,
    string ConsolidatedRationale,
    Dictionary<string, double> SubScoreAverages
);

public class ScoreApplicationCommandHandler : IRequestHandler<ScoreApplicationCommand, ScoreApplicationResult>
{
    private readonly ILlmProxyService _llmService;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IJobRepository _jobRepo;
    private readonly IScoringPromptRepository _promptRepo;

    public ScoreApplicationCommandHandler(
        ILlmProxyService llmService,
        IApplicationRepository applicationRepo,
        IJobRepository jobRepo,
        IScoringPromptRepository promptRepo)
    {
        _llmService = llmService;
        _applicationRepo = applicationRepo;
        _jobRepo = jobRepo;
        _promptRepo = promptRepo;
    }

    public async Task<ScoreApplicationResult> Handle(ScoreApplicationCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptVersionId, ct)
            ?? throw new InvalidOperationException($"Scoring prompt {request.PromptVersionId} not found");

        // Load original document blob (FR-060) — no extraction needed, AWR API handles OCR
        var documents = await _applicationRepo.GetDocumentsAsync(request.ApplicationId, ct);
        if (!documents.Any())
            throw new InvalidOperationException($"No documents found for application {request.ApplicationId}");
        var primaryDoc = documents.First();
        var docBytes = primaryDoc.ContentBase64 != null
            ? Convert.FromBase64String(primaryDoc.ContentBase64)
            : throw new InvalidOperationException($"No document content found for application {request.ApplicationId}");

        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found");

        var jobDescriptionText = job.JobDescription ?? job.Title;

        // Resolve placeholders
        var resolvedPrompt = prompt.PromptText
            .Replace("{{JOB_SPEC_TEXT}}", jobDescriptionText);

        // Send single request with runs parameter (FR-009, FR-062)
        var responseText = await _llmService.ScoreWithDocumentAsync(
            resolvedPrompt, docBytes, primaryDoc.FileName, primaryDoc.FileType, request.RunCount, ct);

        var runs = new List<ScoringRun>();
        EngineAggregatedResult? aggregated = null;

        try
        {
            var jsonText = ExtractJson(responseText);
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            // Check for combined response format (multi-run with aggregated result)
            if (root.TryGetProperty("runs", out var runsElement) && runsElement.ValueKind == JsonValueKind.Array
                && root.TryGetProperty("aggregated", out var aggElement))
            {
                int runIndex = 0;
                foreach (var runElement in runsElement.EnumerateArray())
                {
                    runIndex++;
                    var run = ParseSingleRun(runElement, request.ApplicationId, prompt.Id, runIndex);
                    await _applicationRepo.AddScoringRunAsync(run, ct);
                    runs.Add(run);
                }

                // Parse engine-provided aggregated result
                aggregated = ParseAggregatedResult(aggElement);
            }
            else
            {
                // Fallback: single-run response (backward compatibility)
                var run = ParseSingleRun(root, request.ApplicationId, prompt.Id, 1);
                await _applicationRepo.AddScoringRunAsync(run, ct);
                runs.Add(run);
            }
        }
        catch (JsonException)
        {
            var run = new ScoringRun
            {
                ApplicationId = request.ApplicationId,
                RunIndex = 1,
                TotalScore = 0,
                CategoryScoresJson = "{}",
                MustHaveEvaluationJson = JsonSerializer.Serialize(new
                {
                    error = "Failed to parse LLM response as JSON",
                    rawResponse = responseText
                }),
                EvidenceCitationsJson = "[]",
                ImprovementTipsJson = "[]",
                AiModelId = "passthrough-llm",
                PromptVersion = prompt.Id,
            };
            await _applicationRepo.AddScoringRunAsync(run, ct);
            runs.Add(run);
        }

        return new ScoreApplicationResult(runs, aggregated);
    }

    private ScoringRun ParseSingleRun(JsonElement root, string applicationId, string promptId, int runIndex)
    {
        double totalScore = 0;
        foreach (var key in new[] { "composite_score", "total_score", "overall_score", "score",
            "overall_weighted_score_percentage", "weighted_score_percentage", "total_weighted_score" })
        {
            if (root.TryGetProperty(key, out var cs))
            {
                if (cs.ValueKind == JsonValueKind.Number) { totalScore = cs.GetDouble(); break; }
                if (cs.ValueKind == JsonValueKind.String && double.TryParse(cs.GetString(), out var parsed)) { totalScore = parsed; break; }
            }
        }

        var categoryScores = new Dictionary<string, double>();
        var evidenceCitations = new List<object>();

        JsonElement scoresElement = default;
        bool hasScores = root.TryGetProperty("rubric_scores", out scoresElement)
            || root.TryGetProperty("category_scores", out scoresElement)
            || root.TryGetProperty("scores", out scoresElement)
            || root.TryGetProperty("criteria", out scoresElement);

        if (hasScores)
        {
            if (scoresElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in scoresElement.EnumerateArray())
                {
                    var cat = s.TryGetProperty("category", out var c) ? c.GetString() ?? ""
                        : s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                    double score = 0;
                    foreach (var scoreKey in new[] { "score", "score_0_to_5", "weighted_score", "raw_score" })
                    {
                        if (s.TryGetProperty(scoreKey, out var sc))
                        {
                            if (sc.ValueKind == JsonValueKind.Number) { score = sc.GetDouble(); break; }
                            if (sc.ValueKind == JsonValueKind.String && double.TryParse(sc.GetString(), out var ps)) { score = ps; break; }
                        }
                    }

                    if (!string.IsNullOrEmpty(cat)) categoryScores[cat] = score;

                    string evidenceText = "";
                    if (s.TryGetProperty("evidence", out var ev))
                    {
                        evidenceText = ev.ValueKind == JsonValueKind.String ? ev.GetString() ?? "" : ev.GetRawText();
                    }
                    else if (s.TryGetProperty("key_evidence", out var ke))
                    {
                        if (ke.ValueKind == JsonValueKind.Array)
                        {
                            var items = new List<string>();
                            foreach (var item in ke.EnumerateArray())
                                items.Add(item.GetString() ?? item.ToString());
                            evidenceText = string.Join("; ", items);
                        }
                        else
                        {
                            evidenceText = ke.GetString() ?? ke.GetRawText();
                        }
                    }

                    string concerns = "";
                    if (s.TryGetProperty("concerns_or_gaps", out var cg))
                        concerns = cg.GetString() ?? "";
                    else if (s.TryGetProperty("gaps", out var g))
                        concerns = g.GetString() ?? "";

                    double weight = 0;
                    if (s.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number)
                        weight = w.GetDouble();

                    evidenceCitations.Add(new
                    {
                        category = cat,
                        snippet = evidenceText,
                        concerns,
                        weight,
                        section = s.TryGetProperty("section", out var sec) ? sec.GetString() ?? "" : "",
                        confidence = s.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number ? conf.GetDouble() : 0
                    });
                }
            }
            else if (scoresElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in scoresElement.EnumerateObject())
                {
                    double score = 0;
                    if (prop.Value.ValueKind == JsonValueKind.Number) score = prop.Value.GetDouble();
                    else if (prop.Value.ValueKind == JsonValueKind.String) double.TryParse(prop.Value.GetString(), out score);
                    categoryScores[prop.Name] = score;
                }
            }
        }

        string mustHaveJson = "{}";
        if (root.TryGetProperty("eligibility_gate", out var gate))
        {
            mustHaveJson = gate.GetRawText();
        }
        else if (root.TryGetProperty("overall_recommendation", out var rec))
        {
            var recommendation = rec.GetString() ?? "";
            var passed = recommendation.Contains("suitable", StringComparison.OrdinalIgnoreCase)
                || recommendation.Contains("recommended", StringComparison.OrdinalIgnoreCase)
                || recommendation.Contains("eligible", StringComparison.OrdinalIgnoreCase)
                || recommendation.Contains("pass", StringComparison.OrdinalIgnoreCase);
            mustHaveJson = JsonSerializer.Serialize(new { passed, recommendation, missing_criteria = Array.Empty<string>() });
        }

        var tips = new List<string>();
        JsonElement recsElement = default;
        bool hasRecs = root.TryGetProperty("improvement_recommendations", out recsElement)
            || root.TryGetProperty("recommendations", out recsElement)
            || root.TryGetProperty("improvement_tips", out recsElement)
            || root.TryGetProperty("areas_for_improvement", out recsElement);

        if (hasRecs && recsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in recsElement.EnumerateArray())
                tips.Add(r.GetString() ?? r.ToString());
        }

        if (!tips.Any() && hasScores && scoresElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scoresElement.EnumerateArray())
            {
                var cat = s.TryGetProperty("name", out var cn) ? cn.GetString() ?? ""
                    : s.TryGetProperty("category", out var cc) ? cc.GetString() ?? "" : "";
                string concern = "";
                if (s.TryGetProperty("concerns_or_gaps", out var cg2))
                    concern = cg2.GetString() ?? "";
                else if (s.TryGetProperty("gaps", out var g2))
                    concern = g2.GetString() ?? "";
                if (!string.IsNullOrEmpty(concern) && !concern.Contains("None", StringComparison.OrdinalIgnoreCase))
                    tips.Add(string.Format("{0}: {1}", cat, concern));
            }
        }

        var run = new ScoringRun
        {
            ApplicationId = applicationId,
            RunIndex = runIndex,
            TotalScore = totalScore,
            CategoryScoresJson = JsonSerializer.Serialize(categoryScores),
            MustHaveEvaluationJson = mustHaveJson,
            EvidenceCitationsJson = JsonSerializer.Serialize(evidenceCitations),
            ImprovementTipsJson = JsonSerializer.Serialize(tips),
            AiModelId = "passthrough-llm",
            PromptVersion = promptId,
            InputTokens = 0,
            OutputTokens = 0,
        };

        if (totalScore == 0 && !categoryScores.Any() && mustHaveJson == "{}")
        {
            run.MustHaveEvaluationJson = JsonSerializer.Serialize(new
            {
                error = "LLM response parsed as JSON but contained no recognized scoring fields",
                rawResponse = root.GetRawText()
            });
        }

        return run;
    }

    private static EngineAggregatedResult ParseAggregatedResult(JsonElement aggElement)
    {
        var finalScore = aggElement.TryGetProperty("final_score", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble() : 0;
        var variance = aggElement.TryGetProperty("variance", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var confidence = aggElement.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
        var finalDecision = aggElement.TryGetProperty("final_decision", out var fd) ? fd.GetString() ?? "Excluded" : "Excluded";
        var consolidatedRationale = aggElement.TryGetProperty("consolidated_rationale", out var cr) ? cr.GetString() ?? "" : "";

        var subScoreAverages = new Dictionary<string, double>();
        if (aggElement.TryGetProperty("sub_score_averages", out var ssa) && ssa.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ssa.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    subScoreAverages[prop.Name] = prop.Value.GetDouble();
            }
        }

        return new EngineAggregatedResult(finalScore, variance, confidence, finalDecision, consolidatedRationale, subScoreAverages);
    }
    private static string ExtractJson(string text)
    {
        // Strip markdown code fences: ```json ... ``` or ``` ... ```
        var fenceMatch = System.Text.RegularExpressions.Regex.Match(
            text, @"```(?:json)?\s*\n?([\s\S]*?)\n?```");
        if (fenceMatch.Success)
            return fenceMatch.Groups[1].Value.Trim();

        // Try to find a JSON object in the response
        var braceStart = text.IndexOf('{');
        var braceEnd = text.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
            return text[braceStart..(braceEnd + 1)];

        return text.Trim();
    }
}
