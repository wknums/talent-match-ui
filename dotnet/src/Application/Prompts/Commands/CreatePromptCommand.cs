using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record CreatePromptCommand(
    string JobId,
    string PromptText,
    string Source,
    string? GenerationMetadataJson,
    string Author
) : IRequest<ScoringPrompt>;

public class CreatePromptCommandHandler : IRequestHandler<CreatePromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IJobRepository _jobRepo;

    public CreatePromptCommandHandler(IScoringPromptRepository promptRepo, IJobRepository jobRepo)
    {
        _promptRepo = promptRepo;
        _jobRepo = jobRepo;
    }

    public async Task<ScoringPrompt> Handle(CreatePromptCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException("Job not found");

        var existing = await _promptRepo.GetByJobIdAsync(request.JobId, ct);
        var maxVersion = existing.Any() ? existing.Max(p => p.VersionNumber) : 0;

        var prompt = new ScoringPrompt
        {
            JobId = request.JobId,
            VersionNumber = maxVersion + 1,
            PromptText = request.PromptText,
            Status = "draft",
            Author = request.Author,
            Source = request.Source,
            GenerationMetadataJson = request.GenerationMetadataJson
        };

        await _promptRepo.AddAsync(prompt, ct);
        return prompt;
    }
}
