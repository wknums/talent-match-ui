using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record EditPromptCommand(
    string PromptId,
    string PromptText,
    string Author,
    string ModelId = "o3",
    string ReasoningLevel = "high"
) : IRequest<ScoringPrompt>;

public class EditPromptCommandHandler : IRequestHandler<EditPromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IScoringProfileProvider? _profileProvider;
    private readonly IReasoningModelCatalog? _reasoningModels;

    public EditPromptCommandHandler(
        IScoringPromptRepository promptRepo,
        IScoringProfileProvider? profileProvider = null,
        IReasoningModelCatalog? reasoningModels = null)
    {
        _promptRepo = promptRepo;
        _profileProvider = profileProvider;
        _reasoningModels = reasoningModels;
    }

    public async Task<ScoringPrompt> Handle(EditPromptCommand request, CancellationToken ct)
    {
        var existing = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        var prompts = await _promptRepo.GetByJobIdAsync(existing.JobId, ct);
        var profile = _reasoningModels is null
            ? _profileProvider?.Current
              ?? new ScoringProfile(request.ModelId.Trim(), request.ReasoningLevel.Trim())
            : await _reasoningModels.ValidateAsync(
                request.ModelId,
                request.ReasoningLevel,
                ct);
        var edited = new ScoringPrompt
        {
            JobId = existing.JobId,
            VersionNumber = prompts.Count == 0 ? 1 : prompts.Max(item => item.VersionNumber) + 1,
            PromptText = request.PromptText,
            Status = "draft",
            Author = request.Author,
            Source = "manual",
            GenerationMetadataJson = existing.GenerationMetadataJson,
            GenerationInstructionVersionId = existing.GenerationInstructionVersionId,
            ModelId = profile.ModelId,
            ReasoningLevel = profile.ReasoningLevel,
        };

        await _promptRepo.AddAsync(edited, ct);
        return edited;
    }
}
