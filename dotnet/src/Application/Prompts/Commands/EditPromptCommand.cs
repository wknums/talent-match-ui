using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record EditPromptCommand(
    string PromptId,
    string PromptText,
    string Author
) : IRequest<ScoringPrompt>;

public class EditPromptCommandHandler : IRequestHandler<EditPromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;

    public EditPromptCommandHandler(IScoringPromptRepository promptRepo)
    {
        _promptRepo = promptRepo;
    }

    public async Task<ScoringPrompt> Handle(EditPromptCommand request, CancellationToken ct)
    {
        var existing = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        // FR-035: Create a new revision with incremented version
        var allVersions = await _promptRepo.GetByJobIdAsync(existing.JobId, ct);
        var maxVersion = allVersions.Max(p => p.VersionNumber);

        var newPrompt = new ScoringPrompt
        {
            JobId = existing.JobId,
            VersionNumber = maxVersion + 1,
            PromptText = request.PromptText,
            Status = "draft",
            Author = request.Author,
            Source = existing.Source,
            GenerationMetadataJson = existing.GenerationMetadataJson
        };

        await _promptRepo.AddAsync(newPrompt, ct);
        return newPrompt;
    }
}
