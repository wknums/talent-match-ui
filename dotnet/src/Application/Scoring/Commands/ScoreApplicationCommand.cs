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

public record ScoreParsingDiagnostics(
    bool FallbackParsingActivated,
    bool EligibilityFallbackActivated,
    bool TotalScoreFallbackActivated,
    bool GateDetected,
    string EligibilityPath
);

public record ScoreRunParseResult(
    ScoringRun Run,
    ScoreParsingDiagnostics Diagnostics
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

                var parsed = ParseSingleRunWithDiagnostics(root, request.ApplicationId, prompt.Id, runIndex);
                var run = parsed.Run;
                run.RawResponseText = responseText;
                run.RawParsedResponseJson = jsonText;
                run.ParserWarningsJson = BuildParserWarningsJson(parsed.Diagnostics);
                run.ParserConfidence = parsed.Diagnostics.FallbackParsingActivated ? 0.7 : 1.0;
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
                    RawResponseText = responseText,
                    RawParsedResponseJson = null,
                    ParserWarningsJson = JsonSerializer.Serialize(new[] { "invalid_json_response" }),
                    ParserConfidence = 0,
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
        => ParseSingleRunWithDiagnostics(root, applicationId, promptId, runIndex).Run;

    /// <summary>
    /// Platform-mode reuses the same parsing logic without instantiating the handler.
    /// Kept logically identical to the instance method body.
    /// </summary>
    public static ScoringRun ParseSingleRunStatic(JsonElement root, string applicationId, string promptId, int runIndex)
        => ParseSingleRunWithDiagnostics(root, applicationId, promptId, runIndex).Run;

    public static ScoreRunParseResult ParseSingleRunWithDiagnostics(JsonElement root, string applicationId, string promptId, int runIndex)
    {
        var categoryScores = new Dictionary<string, double>();
        var evidenceCitations = new List<object>();
        double totalScore = 0;
        string recommendation = "";
        string notes = "";
        var tips = new List<string>();
        JsonElement? gateElement = null; // captured eligibility gate (any key name)
        var gateDetected = false;
        var eligibilityPath = "none";
        var eligibilityFallbackActivated = false;
        var totalScoreFallbackActivated = false;

        // Classify every top-level property by its value type
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name;
            var val = prop.Value;

            // Intercept gate-like keys before generic handling
            if (LooksLikeEligibilityGate(name) && (val.ValueKind == JsonValueKind.Object || val.ValueKind == JsonValueKind.Array))
            {
                gateElement = val;
                gateDetected = true;
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
            var gateMissingCriteria = ExtractMissingCriteria(gate);
            var gatePassedHint = ExtractGatePassedHint(gate);
            if (gate.ValueKind == JsonValueKind.Array)
            {
                eligibilityPath = "gate_array";
                // Normalize array format: [{criterion, met/passed, evidence}, ...] → structured object
                var entries = new List<object>();
                var missing = new List<string>();
                bool allPassed = true;
                foreach (var item in gate.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var criterion = ExtractGateCriterion(item);
                    var ev = ExtractGateEvidence(item);
                    var met = TryExtractGateEntryStatus(item, out var explicitStatus)
                        ? explicitStatus
                        : InferGateEntryStatus(criterion, ev, gateMissingCriteria, gatePassedHint);
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
                var nestedArray = FindGateEntriesArray(gate);

                if (nestedArray.HasValue)
                {
                    eligibilityPath = "gate_object_nested";
                    // Parse the nested array as gate entries
                    var entries = new List<object>();
                    var missing = new List<string>();
                    bool allPassed = true;
                    foreach (var item in nestedArray.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var criterion = ExtractGateCriterion(item);
                        var ev = ExtractGateEvidence(item);
                        var met = TryExtractGateEntryStatus(item, out var explicitStatus)
                            ? explicitStatus
                            : InferGateEntryStatus(criterion, ev, gateMissingCriteria, gatePassedHint);
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
                        eligibilityPath = "gate_object_raw";
                        mustHaveJson = gate.GetRawText();
                    }
                }
                else
                {
                    eligibilityPath = "gate_object_raw";
                    mustHaveJson = gate.GetRawText();
                }
            }
            else
            {
                eligibilityPath = "gate_raw";
                mustHaveJson = gate.GetRawText();
            }
        }
        else if (!string.IsNullOrEmpty(recommendation))
        {
            eligibilityPath = "recommendation_fallback";
            eligibilityFallbackActivated = true;
            var passed = LooksLikePositiveRecommendation(recommendation);
            mustHaveJson = JsonSerializer.Serialize(new { passed, recommendation, missing_criteria = Array.Empty<string>() });
        }
        else
        {
            eligibilityPath = "none";
            mustHaveJson = "{}";
        }

        // Fallback: if no explicit total score was found, average category scores
        if (totalScore == 0 && categoryScores.Count > 0)
        {
            totalScore = categoryScores.Values.Average();
            totalScoreFallbackActivated = true;
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

        var diagnostics = new ScoreParsingDiagnostics(
            FallbackParsingActivated: eligibilityFallbackActivated || totalScoreFallbackActivated,
            EligibilityFallbackActivated: eligibilityFallbackActivated,
            TotalScoreFallbackActivated: totalScoreFallbackActivated,
            GateDetected: gateDetected,
            EligibilityPath: eligibilityPath);

        return new ScoreRunParseResult(run, diagnostics);
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
        double? firstNumeric = null;
        double? bestNonWeight = null;

        foreach (var prop in obj.EnumerateObject())
        {
            if (!TryGetNumericValue(prop.Value, out var numericValue))
                continue;

            var normalizedFieldName = NormalizeName(prop.Name);
            if (LooksLikeScoreFieldName(normalizedFieldName))
                return numericValue;

            if (!firstNumeric.HasValue)
                firstNumeric = numericValue;

            if (!bestNonWeight.HasValue && !LooksLikeWeightFieldName(normalizedFieldName))
                bestNonWeight = numericValue;
        }

        return bestNonWeight ?? firstNumeric;
    }

    private static bool TryGetNumericValue(JsonElement value, out double parsed)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            parsed = value.GetDouble();
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (raw.EndsWith("%", StringComparison.Ordinal))
                    raw = raw[..^1].Trim();

                if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return true;
                }
            }
        }

        parsed = 0;
        return false;
    }

    private static bool LooksLikeScoreFieldName(string normalizedFieldName)
        => ContainsAny(normalizedFieldName,
            "score",
            "rating",
            "percent",
            "percentage",
            "pct",
            "point",
            "points",
            "mark");

    private static bool LooksLikeWeightFieldName(string normalizedFieldName)
        => ContainsAny(normalizedFieldName,
            "weight",
            "weighting",
            "coefficient",
            "ratio",
            "portion");

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
        if (!TryGetPropertyCaseInsensitive(obj, key, out var val))
            return false;

        if (TryParseFlexibleBoolValue(val, out var parsed))
            return parsed;

        // If the gate key exists with non-empty text and no explicit negative indicator,
        // prefer treating it as satisfied (common LLM output style for gate checks).
        if (val.ValueKind == JsonValueKind.String)
        {
            var text = val.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return true;
        }

        return false;
    }

    private static string ExtractGateCriterion(JsonElement item)
    {
        if (TryGetPropertyCaseInsensitive(item, "criterion", out var criterion) && criterion.ValueKind == JsonValueKind.String)
            return criterion.GetString() ?? "";
        if (TryGetPropertyCaseInsensitive(item, "requirement", out var requirement) && requirement.ValueKind == JsonValueKind.String)
            return requirement.GetString() ?? "";
        if (TryGetPropertyCaseInsensitive(item, "name", out var name) && name.ValueKind == JsonValueKind.String)
            return name.GetString() ?? "";
        if (TryGetPropertyCaseInsensitive(item, "item", out var itemName) && itemName.ValueKind == JsonValueKind.String)
            return itemName.GetString() ?? "";
        return "";
    }

    private static string ExtractGateEvidence(JsonElement item)
    {
        if (!TryGetPropertyCaseInsensitive(item, "evidence", out var evidence))
            return "";

        if (evidence.ValueKind == JsonValueKind.String)
            return evidence.GetString() ?? "";

        if (evidence.ValueKind == JsonValueKind.Array)
        {
            var snippets = evidence
                .EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s));

            return string.Join("; ", snippets!);
        }

        return evidence.GetRawText();
    }

    private static bool TryExtractGateEntryStatus(JsonElement item, out bool passed)
    {
        foreach (var key in new[] { "met", "passed", "satisfied", "eligible", "status", "result", "is_met", "isMet" })
        {
            if (TryGetPropertyCaseInsensitive(item, key, out var value) && TryParseFlexibleBoolValue(value, out var parsed))
            {
                passed = parsed;
                return true;
            }
        }

        passed = false;
        return false;
    }

    private static bool InferGateEntryStatus(string criterion, string evidence, HashSet<string> missingCriteria, bool? gatePassedHint)
    {
        if (IsCriterionInMissingList(criterion, missingCriteria))
            return false;

        if (!string.IsNullOrWhiteSpace(evidence))
        {
            if (LooksLikeNegativeEvidence(evidence))
                return false;

            return true;
        }

        if (gatePassedHint.HasValue)
            return gatePassedHint.Value;

        return false;
    }

    private static HashSet<string> ExtractMissingCriteria(JsonElement gate)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (gate.ValueKind != JsonValueKind.Object)
            return missing;

        foreach (var key in new[] { "missing_criteria", "missingCriteria", "missing", "failed_criteria", "unmet_criteria" })
        {
            if (!TryGetPropertyCaseInsensitive(gate, key, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    missing.Add(text.Trim());
            }
        }

        return missing;
    }

    private static bool? ExtractGatePassedHint(JsonElement gate)
    {
        if (gate.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "passed", "met", "eligible", "satisfied" })
        {
            if (TryGetPropertyCaseInsensitive(gate, key, out var value) && TryParseFlexibleBoolValue(value, out var parsed))
                return parsed;
        }

        if (TryGetPropertyCaseInsensitive(gate, "recommendation", out var recommendation)
            && recommendation.ValueKind == JsonValueKind.String)
        {
            var recText = recommendation.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(recText))
            {
                var normalized = NormalizeForBoolParsing(recText);
                if (ContainsAny(normalized, "excluded", "reject", "rejected", "fail", "failed", "not eligible", "ineligible"))
                    return false;
                if (LooksLikePositiveRecommendation(recText))
                    return true;
            }
        }

        return null;
    }

    private static JsonElement? FindGateEntriesArray(JsonElement gate)
    {
        if (gate.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in gate.EnumerateObject())
        {
            if (IsObjectArray(prop.Value))
                return prop.Value;

            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var nested in prop.Value.EnumerateObject())
            {
                if (IsObjectArray(nested.Value))
                    return nested.Value;
            }
        }

        return null;
    }

    private static bool IsObjectArray(JsonElement value)
        => value.ValueKind == JsonValueKind.Array
           && value.GetArrayLength() > 0
           && value[0].ValueKind == JsonValueKind.Object;

    private static bool IsCriterionInMissingList(string criterion, HashSet<string> missingCriteria)
    {
        if (string.IsNullOrWhiteSpace(criterion) || missingCriteria.Count == 0)
            return false;

        var normalizedCriterion = NormalizeForBoolParsing(criterion);
        if (string.IsNullOrWhiteSpace(normalizedCriterion))
            return false;

        foreach (var missing in missingCriteria)
        {
            var normalizedMissing = NormalizeForBoolParsing(missing);
            if (string.IsNullOrWhiteSpace(normalizedMissing))
                continue;

            if (normalizedMissing == normalizedCriterion
                || normalizedMissing.Contains(normalizedCriterion, StringComparison.Ordinal)
                || normalizedCriterion.Contains(normalizedMissing, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeNegativeEvidence(string evidence)
    {
        var normalized = NormalizeForBoolParsing(evidence);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return ContainsAny(normalized,
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
            "n a");
    }

    private static bool TryParseFlexibleBoolValue(JsonElement value, out bool parsed)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                parsed = true;
                return true;
            case JsonValueKind.False:
                parsed = false;
                return true;
            case JsonValueKind.Number:
                if (value.TryGetDouble(out var number))
                {
                    parsed = number != 0;
                    return true;
                }
                break;
            case JsonValueKind.String:
            {
                var raw = value.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    break;

                var normalized = NormalizeForBoolParsing(raw);

                if (ContainsAny(normalized,
                        "not met", "does not meet", "do not meet", "failed", "fail", "false", "no", "ineligible", "missing", "unmet", "unsatisfied", "non compliant", "noncompliant"))
                {
                    parsed = false;
                    return true;
                }

                if (ContainsAny(normalized,
                        "met", "meets", "passed", "pass", "true", "yes", "eligible", "satisfied", "compliant", "success", "successful"))
                {
                    parsed = true;
                    return true;
                }

                // For structured values like "1" / "0" embedded in text.
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

                break;
            }
        }

        parsed = false;
        return false;
    }

    private static string NormalizeForBoolParsing(string text)
    {
        var chars = text
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(token => text.Contains(token, StringComparison.Ordinal));

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

    private static string? BuildParserWarningsJson(ScoreParsingDiagnostics diagnostics)
    {
        var warnings = new List<string>();

        if (diagnostics.EligibilityFallbackActivated)
            warnings.Add("eligibility_fallback_activated");
        if (diagnostics.TotalScoreFallbackActivated)
            warnings.Add("total_score_fallback_activated");
        if (!diagnostics.GateDetected)
            warnings.Add("eligibility_gate_not_detected");

        return warnings.Count == 0 ? null : JsonSerializer.Serialize(warnings);
    }

    public static string ExtractJsonFromResponse(string text) => ExtractJson(text);

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
