using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record CreatePromptCommand(
    string JobId,
    string PromptText,
    string Source,
    string? GenerationMetadataJson,
    string Author,
    string ModelId = "o3",
    string ReasoningLevel = "high"
) : IRequest<ScoringPrompt>;

public class CreatePromptCommandHandler : IRequestHandler<CreatePromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IJobRepository _jobRepo;
    private readonly IScoringProfileProvider? _profileProvider;
    private readonly IReasoningModelCatalog? _reasoningModels;

    public CreatePromptCommandHandler(
        IScoringPromptRepository promptRepo,
        IJobRepository jobRepo,
        IScoringProfileProvider? profileProvider = null,
        IReasoningModelCatalog? reasoningModels = null)
    {
        _promptRepo = promptRepo;
        _jobRepo = jobRepo;
        _profileProvider = profileProvider;
        _reasoningModels = reasoningModels;
    }

    public async Task<ScoringPrompt> Handle(CreatePromptCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException("Job not found");

        var existing = await _promptRepo.GetByJobIdAsync(request.JobId, ct);
        var maxVersion = existing.Any() ? existing.Max(p => p.VersionNumber) : 0;
        var profile = _reasoningModels is null
            ? _profileProvider?.Current
              ?? new ScoringProfile(request.ModelId.Trim(), request.ReasoningLevel.Trim())
            : await _reasoningModels.ValidateAsync(
                request.ModelId,
                request.ReasoningLevel,
                ct);

        var prompt = new ScoringPrompt
        {
            JobId = request.JobId,
            VersionNumber = maxVersion + 1,
            PromptText = request.PromptText,
            Status = "draft",
            Author = request.Author,
            Source = request.Source,
            GenerationMetadataJson = request.GenerationMetadataJson,
            ModelId = profile.ModelId,
            ReasoningLevel = profile.ReasoningLevel
        };

        await _promptRepo.AddAsync(prompt, ct);
        return prompt;
    }
}
