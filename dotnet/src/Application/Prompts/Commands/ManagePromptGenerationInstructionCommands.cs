using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public sealed record CreatePromptGenerationInstructionCommand(
    string? JobId,
    string InstructionText,
    string? ChangeNote,
    string Actor,
    string ModelId = "o3",
    string ReasoningLevel = "high") : IRequest<PromptGenerationInstruction>;

public sealed class CreatePromptGenerationInstructionCommandHandler(
    IPromptGenerationInstructionRepository repository,
    IReasoningModelCatalog? reasoningModels = null)
    : IRequestHandler<CreatePromptGenerationInstructionCommand, PromptGenerationInstruction>
{
    public async Task<PromptGenerationInstruction> Handle(
        CreatePromptGenerationInstructionCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InstructionText))
            throw new InvalidOperationException("Generation instruction text is required.");
        var profile = reasoningModels is null
            ? new ScoringProfile(request.ModelId.Trim(), request.ReasoningLevel.Trim())
            : await reasoningModels.ValidateAsync(
                request.ModelId,
                request.ReasoningLevel,
                cancellationToken);

        var instruction = new PromptGenerationInstruction
        {
            JobId = request.JobId,
            VersionNumber = await repository.GetNextVersionNumberAsync(
                request.JobId, cancellationToken),
            InstructionText = request.InstructionText.Trim(),
            ModelId = profile.ModelId,
            ReasoningLevel = profile.ReasoningLevel,
            ChangeNote = string.IsNullOrWhiteSpace(request.ChangeNote)
                ? null
                : request.ChangeNote.Trim(),
            CreatedBy = request.Actor,
        };
        await repository.AddAsync(instruction, cancellationToken);
        return instruction;
    }
}

public sealed record ActivatePromptGenerationInstructionCommand(
    string InstructionId,
    string? ExpectedJobId,
    string Actor) : IRequest<PromptGenerationInstruction>;

public sealed class ActivatePromptGenerationInstructionCommandHandler(
    IPromptGenerationInstructionRepository repository)
    : IRequestHandler<ActivatePromptGenerationInstructionCommand, PromptGenerationInstruction>
{
    public async Task<PromptGenerationInstruction> Handle(
        ActivatePromptGenerationInstructionCommand request,
        CancellationToken cancellationToken)
    {
        var selected = await repository.GetByIdAsync(
            request.InstructionId, cancellationToken)
            ?? throw new InvalidOperationException("Generation instruction not found.");
        if (!string.Equals(selected.JobId, request.ExpectedJobId, StringComparison.Ordinal))
            throw new InvalidOperationException("Generation instruction scope does not match.");

        var active = await repository.GetActiveAsync(
            selected.JobId, cancellationToken);
        if (active is not null && active.Id != selected.Id)
        {
            active.Status = "inactive";
            await repository.UpdateAsync(active, cancellationToken);
        }

        selected.Status = "active";
        selected.ActivatedAt = DateTime.UtcNow;
        selected.ActivatedBy = request.Actor;
        await repository.UpdateAsync(selected, cancellationToken);
        return selected;
    }
}
