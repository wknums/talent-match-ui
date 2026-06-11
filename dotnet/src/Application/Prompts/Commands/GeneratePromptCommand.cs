using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record GeneratePromptCommand(
    string JobId,
    string Author
) : IRequest<ScoringPrompt>;

public class GeneratePromptCommandHandler : IRequestHandler<GeneratePromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IJobRepository _jobRepo;
    private readonly ILlmProxyService? _llmService;

    // Identical to Stack A system prompt in server/routes/prompts.ts
    private const string SystemPrompt = """
        You are an expert at creating scoring prompts for candidate evaluation.
        Given an approved job rubric and a target scoring JSON template derived from that rubric, create a structured scoring prompt that an AI model can use to evaluate candidate applications.
        The prompt should:
        1. Evaluate candidates across all rubric categories with the specified weights
        2. Check all must-have criteria
        3. Consider desired qualifications
        4. Consider all other categories and criteria provided in the rubric and job config
        5. Produce scores from 0-100 for each category
        6. Provide evidence citations from the candidate's documents
        7. Include improvement recommendations
        8. Include all eligibility gate details regardless if they are met or not.
        9. Require the scorer to return JSON that matches the provided target template shape exactly.
        Return ONLY the scoring prompt text, ready for use.
        At the end of the prompt, include the instruction to return all output as complete and valid JSON corresponding exactly to the provided rubric-derived template, with no missing keys or partial arrays.
        """;

    public GeneratePromptCommandHandler(
        IScoringPromptRepository promptRepo,
        IJobRepository jobRepo,
        ILlmProxyService? llmService = null)
    {
        _promptRepo = promptRepo;
        _jobRepo = jobRepo;
        _llmService = llmService;
    }

    public async Task<ScoringPrompt> Handle(GeneratePromptCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException("Job not found");

        // Prefer the latest approved rubric config. For backward compatibility with
        // older jobs that predate approval status, fall back to the latest config
        // that still contains rubric categories.
        var approvedConfig = job.ConfigVersions
            .Where(v => string.Equals(v.RubricApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();

        var config = approvedConfig ?? job.ConfigVersions
            .Where(v => ContainsRubricCategories(v.RubricJson))
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();

        if (config == null)
            throw new InvalidOperationException("Job must have a rubric to generate a prompt");

        var rubricCategories = JsonSerializer.Deserialize<List<RubricCategoryDto>>(config.RubricJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        if (rubricCategories.Count == 0)
            throw new InvalidOperationException("Job must have an approved rubric to generate a prompt");

        var mustHaves = new List<MustHaveDto>();
        if (!string.IsNullOrEmpty(config.MustHavesJson))
        {
            var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = JsonDocument.Parse(config.MustHavesJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                if (first.ValueKind == JsonValueKind.String)
                {
                    // Flat string array: ["criterion1", "criterion2"]
                    mustHaves = doc.RootElement.EnumerateArray()
                        .Select(el => new MustHaveDto { Criterion = el.GetString() ?? "", Description = "" })
                        .ToList();
                }
                else
                {
                    // Object array: [{ criterion, description }]
                    mustHaves = JsonSerializer.Deserialize<List<MustHaveDto>>(config.MustHavesJson, jsonOpts) ?? [];
                }
            }
        }

        var desiredCriteria = JsonSerializer.Deserialize<List<DesiredCriteriaDto>>(config.DesiredCriteriaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        var targetScoringJson = BuildTargetScoringJsonTemplate(rubricCategories, mustHaves, desiredCriteria);

        // Build rubric context — identical structure to Stack A
        var rubricContext = new
        {
            jobTitle = job.Title,
            department = job.Department,
            organization = job.Organisation,
            rubricCategories = rubricCategories.Select(c => new
            {
                name = c.Name,
                weight = c.Weight,
                description = c.Description
            }),
            mustHaves = mustHaves.Select(mh => new
            {
                criterion = mh.Criterion,
                description = mh.Description
            }),
            desiredCriteria = desiredCriteria.Select(dc => new
            {
                qualification = dc.Qualification,
                description = dc.Description
            }),
            targetScoringJson
        };

        string promptText;
        string? metadataJson = null;

        if (_llmService != null)
        {
            // FR-034: Generate prompt from rubric using LLM
            var userPrompt = JsonSerializer.Serialize(rubricContext, new JsonSerializerOptions { WriteIndented = true });

            promptText = await _llmService.SendPromptAsync(SystemPrompt, userPrompt, ct);
            metadataJson = JsonSerializer.Serialize(new
            {
                GeneratedBy = "llm",
                Source = "AWR_SEQ_API",
                GeneratedAt = DateTime.UtcNow
            });
        }
        else
        {
            // Fallback — matches Stack A generateFallbackPrompt()
            promptText = GenerateFallbackPrompt(job, rubricCategories, mustHaves, desiredCriteria);
            metadataJson = JsonSerializer.Serialize(new
            {
                GeneratedBy = "fallback",
                Reason = "LLM service not available",
                GeneratedAt = DateTime.UtcNow
            });
        }

        promptText = AppendStableOutputContract(promptText, targetScoringJson);

        var existing = await _promptRepo.GetByJobIdAsync(request.JobId, ct);
        var maxVersion = existing.Any() ? existing.Max(p => p.VersionNumber) : 0;

        var prompt = new ScoringPrompt
        {
            JobId = request.JobId,
            VersionNumber = maxVersion + 1,
            PromptText = promptText,
            Status = "draft",
            Author = request.Author,
            Source = "generated",
            GenerationMetadataJson = metadataJson
        };

        await _promptRepo.AddAsync(prompt, ct);
        return prompt;
    }

    // Matches Stack A generateFallbackPrompt() in server/routes/prompts.ts
    private static string GenerateFallbackPrompt(
        Job job,
        List<RubricCategoryDto> rubricCategories,
        List<MustHaveDto> mustHaves,
        List<DesiredCriteriaDto> desiredCriteria)
    {
        var categories = string.Join("\n",
            rubricCategories.Select(c => $"- {c.Name} (weight: {c.Weight * 100:F0}%): {c.Description}"));

        var mustHaveLines = mustHaves.Count > 0
            ? string.Join("\n", mustHaves.Select(mh => $"- {mh.Criterion}: {mh.Description}"))
            : "None specified";

        var desiredLines = desiredCriteria.Count > 0
            ? string.Join("\n", desiredCriteria.Select(dc => $"- {dc.Qualification}: {dc.Description}"))
            : "None specified";

        return $"""
            You are evaluating a candidate for the position of "{job.Title}" in the {job.Department} department at {job.Organisation}.

            ## Scoring Categories
            {categories}

            ## Must-Have Criteria (Pass/Fail)
            {mustHaveLines}

            ## Desired Qualifications
            {desiredLines}

            ## Instructions
            1. Score each category from 0-100 based on evidence from the candidate's documents
            2. For each must-have criterion, determine PASS or FAIL with justification
            3. Note any desired qualifications that are met
            4. Provide specific evidence citations from the documents
            5. Calculate a weighted overall score
            6. Provide improvement recommendations

            Respond with your evaluation as complete and valid JSON.
            """;
    }

    private static string BuildTargetScoringJsonTemplate(
        List<RubricCategoryDto> rubricCategories,
        List<MustHaveDto> mustHaves,
        List<DesiredCriteriaDto> desiredCriteria)
    {
        var template = new
        {
            overall_score = 0,
            category_scores = rubricCategories.Select(c => new
            {
                name = c.Name,
                weight = c.Weight,
                description = c.Description,
                score = 0,
                evidence = Array.Empty<string>()
            }),
            must_have_requirements = new
            {
                passed = false,
                recommendation = "",
                missing_criteria = Array.Empty<string>(),
                details = new
                {
                    entries = mustHaves.Select(mh => new
                    {
                        criterion = mh.Criterion,
                        description = mh.Description,
                        passed = false,
                        evidence = ""
                    })
                }
            },
            desired_criteria = desiredCriteria.Select(dc => new
            {
                qualification = dc.Qualification,
                description = dc.Description,
                met = false,
                evidence = ""
            }),
            summary_notes = "",
            improvement_tips = Array.Empty<string>()
        };

        return JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string AppendStableOutputContract(string promptText, string targetScoringJson)
    {
        return $"""
            {promptText.TrimEnd()}

            ## Output Contract (Mandatory)
            Return exactly one JSON object that matches the target template below.
            - Keep all top-level keys and nested keys exactly as provided.
            - Keep rubric category names and weights exactly as provided.
            - Populate score, evidence, pass/fail flags, notes, and tips from candidate evidence.
            - Do not add, remove, or rename keys.
            - Do not wrap the JSON in markdown code fences.

            ### Target JSON Template
            {targetScoringJson}
            """;
    }

    private static bool ContainsRubricCategories(string? rubricJson)
    {
        if (string.IsNullOrWhiteSpace(rubricJson))
            return false;

        try
        {
            var categories = JsonSerializer.Deserialize<List<RubricCategoryDto>>(
                rubricJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return categories.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    // DTOs for deserializing JSON stored in JobConfigVersion
    // Use classes (not positional records) so System.Text.Json ignores extra fields like "id"
    private class RubricCategoryDto
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public double Weight { get; set; }
    }

    private class MustHaveDto
    {
        public string Criterion { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private class DesiredCriteriaDto
    {
        public string Qualification { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
