using System.Text.Json;
using System.Text;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Scoring.Commands;

public record ScoreApplicationCommand(
    string ApplicationId,
    string JobId,
    int RunCount,
    string PromptVersionId,
    string JobDescriptionText,
    string? RubricJson = null
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
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IBlobStore _blobStore;

    public ScoreApplicationCommandHandler(
        ILlmProxyService llmService,
        IApplicationRepository applicationRepo,
        IScoringPromptRepository promptRepo,
        IBlobStore blobStore)
    {
        _llmService = llmService;
        _applicationRepo = applicationRepo;
        _promptRepo = promptRepo;
        _blobStore = blobStore;
    }

    private async Task<byte[]?> ResolveDocumentBytesAsync(ApplicationDocument doc, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(doc.BlobUri))
        {
            var fromBlob = await _blobStore.ReadByUriAsync(doc.BlobUri, ct);
            if (fromBlob is { Length: > 0 }) return fromBlob;
        }

        if (string.IsNullOrEmpty(doc.ContentBase64)) return null;

        try { return Convert.FromBase64String(doc.ContentBase64); }
        catch { return Encoding.UTF8.GetBytes(doc.ContentBase64); }
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
        var docBytes = await ResolveDocumentBytesAsync(primaryDoc, ct)
            ?? throw new InvalidOperationException($"No document content found for application {request.ApplicationId}");

        // Resolve placeholders — job description passed in to avoid loading Job with tracked Applications
        var resolvedPrompt = prompt.PromptText
            .Replace("{{JOB_SPEC_TEXT}}", request.JobDescriptionText);

        // Passthrough returns one response per run — loop over all responses
        var responses = await _llmService.ScoreWithDocumentAsync(
            resolvedPrompt, docBytes, primaryDoc.FileName, primaryDoc.FileType, request.RunCount, ct);

        var runs = new List<ScoringRun>();
        EngineAggregatedResult? aggregated = null;

        int runIndex = 0;
        foreach (var responseText in responses)
        {
            runIndex++;
            try
            {
                var jsonText = ExtractJson(responseText);
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                var run = ParseSingleRun(root, request.ApplicationId, prompt.Id, runIndex);
                RemapToRubric(run, request.RubricJson);
                await _applicationRepo.AddScoringRunAsync(run, ct);
                runs.Add(run);
            }
            catch (JsonException)
            {
                // Non-JSON response: store raw output and flag as error so user can fix the prompt
                var run = new ScoringRun
                {
                    ApplicationId = request.ApplicationId,
                    RunIndex = runIndex,
                    TotalScore = 0,
                    CategoryScoresJson = "{}",
                    MustHaveEvaluationJson = JsonSerializer.Serialize(new
                    {
                        error = "LLM response is not valid JSON. Edit the scoring prompt to ensure JSON output.",
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
        }

        return new ScoreApplicationResult(runs, aggregated);
    }

    /// <summary>
    /// Schema-agnostic parser: walks the JSON structure and extracts scores, evidence,
    /// and metadata from whatever structure the LLM returns. No hardcoded field names.
    /// 
    /// Recognises three patterns:
    ///   1. Array of score objects: any array property whose elements contain a numeric "score"-like field
    ///   2. Object-per-category: top-level properties whose value is an object with a numeric "score"-like field
    ///   3. Flat object: top-level properties that are plain numbers (treated as category→score map)
    /// 
    /// Scalar strings are collected as metadata (recommendation, summary, notes).
    /// Scalar numbers are candidates for the total/overall score.
    /// </summary>
    internal ScoringRun ParseSingleRun(JsonElement root, string applicationId, string promptId, int runIndex)
        => ParseSingleRunStatic(root, applicationId, promptId, runIndex);

    /// <summary>
    /// Platform-mode reuses the same parsing logic without instantiating the handler.
    /// Kept logically identical to the instance method body.
    /// </summary>
    public static ScoringRun ParseSingleRunStatic(JsonElement root, string applicationId, string promptId, int runIndex)
    {
        var categoryScores = new Dictionary<string, double>();
        var evidenceCitations = new List<object>();
        double totalScore = 0;
        string recommendation = "";
        string notes = "";
        var tips = new List<string>();
        JsonElement? gateElement = null; // captured eligibility gate (any key name)

        // Classify every top-level property by its value type
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name;
            var val = prop.Value;

            // Intercept gate-like keys before generic handling
            if (LooksLikeEligibilityGate(name) && (val.ValueKind == JsonValueKind.Object || val.ValueKind == JsonValueKind.Array))
            {
                gateElement = val;
                Console.WriteLine($"[GATE DEBUG] Key='{name}' Kind={val.ValueKind} Raw={val.GetRawText()[..Math.Min(500, val.GetRawText().Length)]}");
                continue;
            }

            switch (val.ValueKind)
            {
                case JsonValueKind.Number:
                    // A top-level number is either a total/overall score or a flat category score.
                    // Heuristic: if the name suggests "total/overall/composite/final", treat as total.
                    if (LooksLikeTotalScore(name))
                        totalScore = val.GetDouble();
                    else
                        categoryScores[name] = val.GetDouble();
                    break;

                case JsonValueKind.String:
                    // Collect string metadata
                    var strVal = val.GetString() ?? "";
                    if (LooksLikeTotalScore(name))
                    {
                        // Handle formula strings like "95*0.60 + 80*0.20 + ... = 90.3"
                        var extracted = ExtractNumericFromString(strVal);
                        if (extracted.HasValue)
                            totalScore = extracted.Value;
                    }
                    else if (LooksLikeRecommendation(name))
                        recommendation = strVal;
                    else if (LooksLikeNotes(name))
                        notes = strVal;
                    break;

                case JsonValueKind.Object:
                    // Object with a numeric score sub-property → category score entry
                    var catScore = ExtractNumericField(val);
                    if (catScore.HasValue)
                    {
                        categoryScores[name] = catScore.Value;
                        var evidence = ExtractStringField(val);
                        if (!string.IsNullOrWhiteSpace(evidence))
                            evidenceCitations.Add(new { category = name, snippet = evidence });
                    }
                    else
                    {
                        // Nested category map: { "CategoryName": { "score": 85, "evidence": [...] }, ... }
                        foreach (var subProp in val.EnumerateObject())
                        {
                            if (subProp.Value.ValueKind == JsonValueKind.Object)
                            {
                                var subScore = ExtractNumericField(subProp.Value);
                                if (subScore.HasValue)
                                {
                                    categoryScores[subProp.Name] = subScore.Value;
                                    // Extract evidence array (case-insensitive)
                                    if (TryGetPropertyCaseInsensitive(subProp.Value, "evidence", out var evArr) && evArr.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var ev in evArr.EnumerateArray())
                                        {
                                            if (ev.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ev.GetString()))
                                                evidenceCitations.Add(new { category = subProp.Name, snippet = ev.GetString()! });
                                        }
                                    }
                                    // Extract improvement_recommendations
                                    if (subProp.Value.TryGetProperty("improvement_recommendations", out var tipArr) && tipArr.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var tip in tipArr.EnumerateArray())
                                        {
                                            if (tip.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tip.GetString()))
                                                tips.Add(tip.GetString()!);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    // Array of objects with score fields → rubric scores array.
                    // Array of strings → tips/recommendations.
                    if (val.GetArrayLength() > 0)
                    {
                        var first = val[0];
                        if (first.ValueKind == JsonValueKind.Object && ExtractNumericField(first).HasValue)
                        {
                            foreach (var item in val.EnumerateArray())
                            {
                                var itemScore = ExtractNumericField(item);
                                var catName = ExtractCategoryName(item);
                                if (catName != null && itemScore.HasValue)
                                {
                                    categoryScores[catName] = itemScore.Value;
                                    // Check for evidence array first (handles { "category": "X", "score": 85, "evidence": ["a", "b"] })
                                    if (TryGetPropertyCaseInsensitive(item, "evidence", out var evElement))
                                    {
                                        if (evElement.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var ev in evElement.EnumerateArray())
                                            {
                                                if (ev.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ev.GetString()))
                                                    evidenceCitations.Add(new { category = catName, snippet = ev.GetString()! });
                                            }
                                        }
                                        else if (evElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(evElement.GetString()))
                                        {
                                            evidenceCitations.Add(new { category = catName, snippet = evElement.GetString()! });
                                        }
                                    }
                                    else
                                    {
                                        // Fallback: extract longest string field as evidence
                                        var ev = ExtractStringField(item);
                                        if (!string.IsNullOrWhiteSpace(ev))
                                            evidenceCitations.Add(new { category = catName, snippet = ev });
                                    }
                                }
                            }
                        }
                        else if (first.ValueKind == JsonValueKind.String)
                        {
                            foreach (var item in val.EnumerateArray())
                                tips.Add(item.GetString() ?? "");
                        }
                    }
                    break;
            }
        }

        // Build eligibility / must-have evaluation
        string mustHaveJson;
        if (gateElement.HasValue)
        {
            var gate = gateElement.Value;
            if (gate.ValueKind == JsonValueKind.Array)
            {
                // Normalize array format: [{criterion, met/passed, evidence}, ...] → structured object
                var entries = new List<object>();
                var missing = new List<string>();
                bool allPassed = true;
                foreach (var item in gate.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var criterion = item.TryGetProperty("criterion", out var c) ? c.GetString() ?? ""
                                  : item.TryGetProperty("requirement", out var r) ? r.GetString() ?? ""
                                  : item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var met = LooksLikeTrueValue(item, "met")
                           || LooksLikeTrueValue(item, "passed")
                           || LooksLikeTrueValue(item, "satisfied")
                           || LooksLikeTrueValue(item, "eligible");
                    var ev = item.TryGetProperty("evidence", out var e) ? e.GetString() ?? "" : "";
                    Console.WriteLine($"[GATE ENTRY] criterion='{criterion}' met={met} raw_keys=[{string.Join(",", item.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}"))}]");
                    entries.Add(new { criterion, passed = met, evidence = ev });
                    if (!met)
                    {
                        allPassed = false;
                        if (!string.IsNullOrEmpty(criterion))
                            missing.Add(criterion);
                    }
                }
                mustHaveJson = JsonSerializer.Serialize(new
                {
                    passed = allPassed,
                    recommendation = allPassed ? "Eligible" : "Excluded",
                    missing_criteria = missing,
                    details = new { entries }
                });
            }
            else if (gate.ValueKind == JsonValueKind.Object)
            {
                // Object format — could be {passed: bool, ...} or {criteria: [...], ...}
                // Check if it has a nested array of criteria entries
                JsonElement? nestedArray = null;
                foreach (var prop in gate.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0
                        && prop.Value[0].ValueKind == JsonValueKind.Object)
                    {
                        nestedArray = prop.Value;
                        break;
                    }
                }

                if (nestedArray.HasValue)
                {
                    // Parse the nested array as gate entries
                    var entries = new List<object>();
                    var missing = new List<string>();
                    bool allPassed = true;
                    foreach (var item in nestedArray.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var criterion = item.TryGetProperty("criterion", out var c2) ? c2.GetString() ?? ""
                                      : item.TryGetProperty("requirement", out var r2) ? r2.GetString() ?? ""
                                      : item.TryGetProperty("name", out var n2) ? n2.GetString() ?? "" : "";
                        var met = LooksLikeTrueValue(item, "met")
                               || LooksLikeTrueValue(item, "passed")
                               || LooksLikeTrueValue(item, "satisfied")
                               || LooksLikeTrueValue(item, "eligible");
                        var ev = item.TryGetProperty("evidence", out var e2) ? e2.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(criterion)) continue;
                        entries.Add(new { criterion, passed = met, evidence = ev });
                        if (!met) { allPassed = false; missing.Add(criterion); }
                    }
                    if (entries.Count > 0)
                    {
                        mustHaveJson = JsonSerializer.Serialize(new
                        {
                            passed = allPassed,
                            recommendation = allPassed ? "Eligible" : "Excluded",
                            missing_criteria = missing,
                            details = new { entries }
                        });
                    }
                    else
                    {
                        mustHaveJson = gate.GetRawText();
                    }
                }
                else
                {
                    mustHaveJson = gate.GetRawText();
                }
            }
            else
            {
                mustHaveJson = gate.GetRawText();
            }
        }
        else if (!string.IsNullOrEmpty(recommendation))
        {
            var passed = LooksLikePositiveRecommendation(recommendation);
            mustHaveJson = JsonSerializer.Serialize(new { passed, recommendation, missing_criteria = Array.Empty<string>() });
        }
        else
        {
            mustHaveJson = "{}";
        }

        // Fallback: if no explicit total score was found, average category scores
        if (totalScore == 0 && categoryScores.Count > 0)
        {
            totalScore = categoryScores.Values.Average();
        }

        return new ScoringRun
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
    }

    // --- Generic JSON field extraction helpers (no hardcoded schema) ---

    /// <summary>Find the first numeric property in an object (the "score" field, whatever it's called).</summary>
    /// <summary>
    /// Remap LLM-derived category score keys to the exact rubric category names (like Stack A's remapSubScoresToRubric).
    /// Uses 3-tier fuzzy matching: exact normalised → substring containment → 40%+ word overlap.
    /// Mutates the ScoringRun in place before it is persisted.
    /// </summary>

    /// <summary>Case-insensitive property lookup for JsonElement objects.</summary>
    internal static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static void RemapToRubric(ScoringRun run, string? rubricJson) => RemapToRubricStatic(run, rubricJson);

    public static void RemapToRubricStatic(ScoringRun run, string? rubricJson)
    {
        if (string.IsNullOrEmpty(rubricJson) || rubricJson == "[]")
            return;

        List<RubricCategoryInfo>? rubric;
        try
        {
            rubric = JsonSerializer.Deserialize<List<RubricCategoryInfo>>(rubricJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return; }

        if (rubric == null || rubric.Count == 0)
            return;

        Dictionary<string, double>? scores;
        try { scores = JsonSerializer.Deserialize<Dictionary<string, double>>(run.CategoryScoresJson); }
        catch { return; }

        if (scores == null || scores.Count == 0)
            return;

        var rubricNames = rubric.Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var llmKeys = scores.Keys.ToList();
        var remapped = new Dictionary<string, double>();

        foreach (var rubricName in rubricNames)
        {
            // Exact key match
            if (scores.TryGetValue(rubricName, out var exactScore))
            {
                remapped[rubricName] = exactScore;
                continue;
            }
            // Fuzzy match
            var match = FindBestRubricMatch(rubricName, llmKeys);
            if (match != null && scores.TryGetValue(match, out var fuzzyScore))
                remapped[rubricName] = fuzzyScore;
        }

        if (remapped.Count > 0)
            run.CategoryScoresJson = JsonSerializer.Serialize(remapped);
    }

    private static string? FindBestRubricMatch(string rubricName, List<string> llmKeys)
    {
        var normRubric = NormalizeName(rubricName);

        // 1. Exact normalised match
        var exact = llmKeys.FirstOrDefault(k => NormalizeName(k) == normRubric);
        if (exact != null) return exact;

        // 2. One contains the other
        var contained = llmKeys.FirstOrDefault(k =>
        {
            var normK = NormalizeName(k);
            return normRubric.Contains(normK) || normK.Contains(normRubric);
        });
        if (contained != null) return contained;

        // 3. Significant word overlap (words > 2 chars, ≥40% overlap)
        var rubricWords = new HashSet<string>(
            normRubric.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2));
        string? best = null;
        int bestOverlap = 0;
        foreach (var k in llmKeys)
        {
            var kWords = NormalizeName(k).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2).ToList();
            var overlap = kWords.Count(w => rubricWords.Contains(w));
            if (overlap > bestOverlap && kWords.Count > 0 && (double)overlap / kWords.Count >= 0.4)
            {
                bestOverlap = overlap;
                best = k;
            }
        }
        return best;
    }

    private static string NormalizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            name.ToLowerInvariant()
                .Replace('_', ' ').Replace('-', ' ')
                .Replace("(", " ").Replace(")", " ")
                .Replace("&", " ").Replace("/", " ")
                .Replace(",", " ").Replace(";", " ")
                .Replace(":", " "),
            @"\s+", " ").Trim();

    private record RubricCategoryInfo(string Name, double Weight, string? Description);

    internal static double? ExtractNumericField(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number)
                return prop.Value.GetDouble();
            if (prop.Value.ValueKind == JsonValueKind.String &&
                double.TryParse(prop.Value.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    /// <summary>Find the longest string property in an object (the "evidence/justification" field), preferring semantically named properties.</summary>
    internal static string ExtractStringField(JsonElement obj)
    {
        // Prefer properties whose name contains evidence-related keywords
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var lower = prop.Name.ToLowerInvariant();
                if (lower.Contains("evidence") || lower.Contains("justification") || lower.Contains("rationale") || lower.Contains("snippet"))
                {
                    var s = prop.Value.GetString() ?? "";
                    if (s.Length > 0) return s;
                }
            }
        }
        // Fallback: longest string
        string best = "";
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString() ?? "";
                if (s.Length > best.Length)
                    best = s;
            }
        }
        return best;
    }

    /// <summary>Find a name/category/label string property in an object (for array-of-objects format), preferring semantically named properties.</summary>
    internal static string? ExtractCategoryName(JsonElement obj)
    {
        // Prefer properties whose name contains category/name/label keywords
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var lower = prop.Name.ToLowerInvariant();
                if (lower.Contains("category") || lower.Contains("name") || lower.Contains("label"))
                {
                    var s = prop.Value.GetString() ?? "";
                    if (s.Length > 0 && s.Length < 200)
                        return s;
                }
            }
        }
        // Fallback: first short string field (likely a label, not a long evidence paragraph)
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString() ?? "";
                if (s.Length > 0 && s.Length < 200)
                    return s;
            }
        }
        return null;
    }

    /// <summary>Extract a numeric value from a formula string like "95*0.60 + 80*0.20 + ... = 90.3".</summary>
    private static double? ExtractNumericFromString(string value)
    {
        // Try the last segment after '=' (handles "expr = expr = 90.3")
        var parts = value.Split('=');
        if (parts.Length > 1)
        {
            var lastPart = parts[^1].Trim();
            if (double.TryParse(lastPart, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var fromEquals))
                return fromEquals;
        }
        // Try parsing the entire string as a number
        if (double.TryParse(value.Trim(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var direct))
            return direct;
        return null;
    }

    // --- Name-matching heuristics (case-insensitive substring checks, not hardcoded keys) ---

    private static bool LooksLikeTotalScore(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        return lower.Contains("total") || lower.Contains("overall") || lower.Contains("composite")
            || lower.Contains("final score") || lower.Contains("weighted score");
    }

    private static bool LooksLikeRecommendation(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        return lower.Contains("recommendation") || lower.Contains("decision") || lower.Contains("verdict");
    }

    private static bool LooksLikeNotes(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        return lower.Contains("summary") || lower.Contains("notes") || lower.Contains("rationale")
            || lower.Contains("comment") || lower.Contains("narrative");
    }

    private static bool LooksLikeEligibilityGate(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        return lower.Contains("eligibility") || lower.Contains("must have") || lower.Contains("gate")
            || lower.Contains("requirement") || lower.Contains("mandatory") || lower.Contains("prerequisite");
    }

    /// <summary>
    /// Robustly check if a JSON object has a truthy value for a given key.
    /// Handles: true (bool), "true"/"yes"/"y"/"1"/"met"/"pass"/"passed" (string), 1 (number).
    /// </summary>
    private static bool LooksLikeTrueValue(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var val)) return false;
        return val.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => val.GetString()?.ToLowerInvariant() is "true" or "yes" or "y" or "1" or "met" or "pass" or "passed",
            JsonValueKind.Number => val.GetDouble() != 0,
            _ => false
        };
    }

    private static bool LooksLikePositiveRecommendation(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("recommend") || lower.Contains("suitable") || lower.Contains("eligible")
            || lower.Contains("proceed") || lower.Contains("interview") || lower.Contains("pass")
            || lower.Contains("approve") || lower.Contains("shortlist");
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
