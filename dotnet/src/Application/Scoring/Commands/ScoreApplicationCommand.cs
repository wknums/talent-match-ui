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
) : IRequest<IReadOnlyList<ScoringRun>>;

public class ScoreApplicationCommandHandler : IRequestHandler<ScoreApplicationCommand, IReadOnlyList<ScoringRun>>
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

    public async Task<IReadOnlyList<ScoringRun>> Handle(ScoreApplicationCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptVersionId, ct)
            ?? throw new InvalidOperationException($"Scoring prompt {request.PromptVersionId} not found");

        var extraction = await _applicationRepo.GetExtractionAsync(request.ApplicationId, ct)
            ?? throw new InvalidOperationException($"No extraction artifact found for application {request.ApplicationId}");

        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found");

        var candidateText = extraction.NormalisedText;
        var jobDescriptionText = job.JobDescription ?? job.Title;

        var runs = new List<ScoringRun>();

        for (int i = 0; i < request.RunCount; i++)
        {
            // Resolve placeholders
            var resolvedPrompt = prompt.PromptText
                .Replace("{{JOB_SPEC_TEXT}}", jobDescriptionText)
                .Replace("{{CANDIDATE_CV_TEXT}}", candidateText);

            ScoringRun run;

            try
            {
                var responseText = await _llmService.ScoreAsync(resolvedPrompt, candidateText, ct);

                try
                {
                    using var doc = JsonDocument.Parse(responseText);
                    var root = doc.RootElement;

                    // Parse composite_score
                    var totalScore = root.TryGetProperty("composite_score", out var cs) ? cs.GetDouble() : 0;

                    // Parse rubric_scores → CategoryScoresJson
                    var categoryScores = new Dictionary<string, double>();
                    var evidenceCitations = new List<object>();
                    if (root.TryGetProperty("rubric_scores", out var scores) && scores.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in scores.EnumerateArray())
                        {
                            var cat = s.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
                            var score = s.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0;
                            categoryScores[cat] = score;

                            evidenceCitations.Add(new
                            {
                                category = cat,
                                snippet = s.TryGetProperty("evidence", out var ev) ? ev.GetString() ?? "" : "",
                                section = s.TryGetProperty("section", out var sec) ? sec.GetString() ?? "" : "",
                                confidence = s.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0
                            });
                        }
                    }

                    // Parse eligibility_gate → MustHaveEvaluationJson
                    string mustHaveJson = "{}";
                    if (root.TryGetProperty("eligibility_gate", out var gate))
                    {
                        mustHaveJson = gate.GetRawText();
                    }

                    // Parse improvement_recommendations
                    var tips = new List<string>();
                    if (root.TryGetProperty("improvement_recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in recs.EnumerateArray())
                        {
                            tips.Add(r.GetString() ?? "");
                        }
                    }

                    run = new ScoringRun
                    {
                        ApplicationId = request.ApplicationId,
                        RunIndex = i + 1,
                        TotalScore = totalScore,
                        CategoryScoresJson = JsonSerializer.Serialize(categoryScores),
                        MustHaveEvaluationJson = mustHaveJson,
                        EvidenceCitationsJson = JsonSerializer.Serialize(evidenceCitations),
                        ImprovementTipsJson = JsonSerializer.Serialize(tips),
                        AiModelId = "passthrough-llm",
                        PromptVersion = prompt.Id,
                        InputTokens = 0,
                        OutputTokens = 0,
                    };
                }
                catch (JsonException)
                {
                    // JSON parse failure — create Failed run with raw response
                    run = new ScoringRun
                    {
                        ApplicationId = request.ApplicationId,
                        RunIndex = i + 1,
                        TotalScore = 0,
                        CategoryScoresJson = "{}",
                        MustHaveEvaluationJson = JsonSerializer.Serialize(new { error = "Failed to parse LLM response", rawResponse = responseText }),
                        EvidenceCitationsJson = "[]",
                        ImprovementTipsJson = "[]",
                        AiModelId = "passthrough-llm",
                        PromptVersion = prompt.Id,
                    };
                }
            }
            catch (InvalidOperationException)
            {
                // API errors — rethrow for upstream retry logic
                throw;
            }

            await _applicationRepo.AddScoringRunAsync(run, ct);
            runs.Add(run);
        }

        return runs;
    }
}
