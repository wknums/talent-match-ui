using MediatR;
using System.Text.Json;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.ExtractionInstructions.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.ExtractionInstructions.Commands;

public sealed record CreateExtractionInstructionCommand(
    string InstructionText,
    string? ChangeNote,
    string ModelId = "o3",
    string ReasoningLevel = "high")
    : IRequest<ExtractionInstructionVersionModel>;

public sealed class CreateExtractionInstructionCommandHandler : IRequestHandler<CreateExtractionInstructionCommand, ExtractionInstructionVersionModel>
{
    private readonly IExtractionInstructionRepository _repository;
    private readonly ICurrentUserService _currentUser;
    private readonly IProcessingEventRepository _events;
    private readonly IReasoningModelCatalog? _reasoningModels;

    public CreateExtractionInstructionCommandHandler(
        IExtractionInstructionRepository repository,
        ICurrentUserService currentUser,
        IProcessingEventRepository events,
        IReasoningModelCatalog? reasoningModels = null)
    {
        _repository = repository;
        _currentUser = currentUser;
        _events = events;
        _reasoningModels = reasoningModels;
    }

    public async Task<ExtractionInstructionVersionModel> Handle(CreateExtractionInstructionCommand request, CancellationToken cancellationToken)
    {
        EnsureAdmin();
        if (string.IsNullOrWhiteSpace(request.InstructionText))
            throw new InvalidOperationException("Instruction text is required.");
        var profile = _reasoningModels is null
            ? new ScoringProfile(request.ModelId.Trim(), request.ReasoningLevel.Trim())
            : await _reasoningModels.ValidateAsync(
                request.ModelId,
                request.ReasoningLevel,
                cancellationToken);

        var version = new ExtractionInstructionVersion
        {
            VersionNumber = await _repository.GetNextVersionNumberAsync(cancellationToken),
            InstructionText = request.InstructionText.Trim(),
            ModelId = profile.ModelId,
            ReasoningLevel = profile.ReasoningLevel,
            ProtectedContractVersion = JobExtraction.Services.JobSpecExtractionContractValidator.ProtectedContractVersion,
            Status = "draft",
            ChangeNote = string.IsNullOrWhiteSpace(request.ChangeNote) ? null : request.ChangeNote.Trim(),
            ValidationStatus = "unvalidated",
            ValidationFindingsJson = "[]",
            CreatedBy = _currentUser.UserId ?? _currentUser.Username ?? "unknown",
            ConcurrencyVersion = 1,
        };

        await _repository.AddAsync(version, cancellationToken);
        await _events.AddAsync(new ProcessingEvent
        {
            Actor = version.CreatedBy,
            EventType = "extraction-instruction.created",
            EntityType = "ExtractionInstructionVersion",
            EntityId = version.Id,
            CorrelationId = Guid.NewGuid().ToString(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                versionId = version.Id,
                versionNumber = version.VersionNumber,
                status = version.Status,
                validationStatus = version.ValidationStatus,
                hasChangeNote = version.ChangeNote is not null,
                version.ModelId,
                version.ReasoningLevel
            })
        }, cancellationToken);

        return version.ToModel();
    }

    private void EnsureAdmin()
    {
        if (!_currentUser.IsAdmin)
            throw new UnauthorizedAccessException("Only admins can manage extraction instructions.");
    }
}
