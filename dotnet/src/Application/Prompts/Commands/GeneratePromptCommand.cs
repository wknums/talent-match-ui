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
        Given the following job rubric, create a structured scoring prompt that an AI model can use to evaluate candidate applications.
        The prompt should:
        1. Evaluate candidates across all rubric categories with the specified weights
        2. Check all must-have criteria
        3. Consider desired qualifications
        4. Produce scores from 0-100 for each category
        5. Provide evidence citations from the candidate's documents
        6. Include improvement recommendations
        7. Include all eligibility gate details regardless if they are met or not.
        Return ONLY the scoring prompt text, ready for use.
        At the end of the prompt, include the instruction to return all the output as valid json.
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

        // Get current config version with rubric — matches Stack A validation
        var config = job.ConfigVersions
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();

        var rubricCategories = config != null
            ? JsonSerializer.Deserialize<List<RubricCategoryDto>>(config.RubricJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
            : [];

        if (rubricCategories.Count == 0)
            throw new InvalidOperationException("Job must have an approved rubric to generate a prompt");

        var mustHaves = new List<MustHaveDto>();
        if (config != null && !string.IsNullOrEmpty(config.MustHaveCriteriaJson))
        {
            var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = JsonDocument.Parse(config.MustHaveCriteriaJson);
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
                    mustHaves = JsonSerializer.Deserialize<List<MustHaveDto>>(config.MustHaveCriteriaJson, jsonOpts) ?? [];
                }
            }
        }

        var desiredCriteria = config != null
            ? JsonSerializer.Deserialize<List<DesiredCriteriaDto>>(config.DesiredCriteriaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
            : [];

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
            })
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

            Respond with your evaluation as valid JSON.
            """;
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
