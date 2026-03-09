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

        string promptText;
        string? metadataJson = null;

        if (_llmService != null)
        {
            // FR-034: Generate prompt from rubric using LLM
            var systemPrompt = "You are an expert HR scoring prompt engineer. Generate a detailed scoring prompt for evaluating job applications based on the provided rubric and job description.";

            var userPrompt = $"Generate a scoring prompt for the following job:\n\nTitle: {job.Title}\nDepartment: {job.Department}\nOrganisation: {job.Organisation}\nDescription: {job.JobDescription ?? "Not provided"}";

            promptText = await _llmService.SendPromptAsync(systemPrompt, userPrompt, ct);
            metadataJson = JsonSerializer.Serialize(new
            {
                GeneratedBy = "llm",
                Model = "gpt-4",
                GeneratedAt = DateTime.UtcNow
            });
        }
        else
        {
            // Fallback when LLM service is not available
            promptText = $"Score the following CV/application against the job requirements for {job.Title} at {job.Organisation}. " +
                         $"Evaluate each rubric criterion on a scale of 0-100. " +
                         $"Provide category scores and an overall weighted score. " +
                         $"Return results as JSON with fields: categoryScores (array of {{criterion, score, justification}}), overallScore, summary.";
            metadataJson = JsonSerializer.Serialize(new
            {
                GeneratedBy = "fallback",
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
}
