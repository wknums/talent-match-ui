using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.ExtractionInstructions.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.ExtractionInstructions.Commands;

public sealed record ActivateExtractionInstructionCommand(string VersionId, int ExpectedConcurrencyVersion)
    : IRequest<ExtractionInstructionVersionModel>;

public sealed class ActivateExtractionInstructionCommandHandler : IRequestHandler<ActivateExtractionInstructionCommand, ExtractionInstructionVersionModel>
{
    private readonly IExtractionInstructionRepository _repository;
    private readonly ICurrentUserService _currentUser;
    private readonly IProcessingEventRepository _events;

    public ActivateExtractionInstructionCommandHandler(
        IExtractionInstructionRepository repository,
        ICurrentUserService currentUser,
        IProcessingEventRepository events)
    {
        _repository = repository;
        _currentUser = currentUser;
        _events = events;
    }

    public async Task<ExtractionInstructionVersionModel> Handle(ActivateExtractionInstructionCommand request, CancellationToken cancellationToken)
    {
        EnsureAdmin();
        var version = await _repository.GetByIdAsync(request.VersionId, cancellationToken)
            ?? throw new InvalidOperationException($"Instruction version '{request.VersionId}' was not found.");

        if (version.ConcurrencyVersion != request.ExpectedConcurrencyVersion)
            throw new InvalidOperationException("stale_version: the instruction version has changed and must be reloaded before activation.");
        if (!string.Equals(version.ValidationStatus, "valid", StringComparison.Ordinal))
            throw new InvalidOperationException("Only validated instruction versions can be activated.");

        var previousActive = await _repository.GetActiveAsync(cancellationToken);
        var actor = _currentUser.UserId ?? _currentUser.Username ?? "unknown";
        var activatedAt = DateTime.UtcNow;
        await _repository.ActivateAsync(version.Id, actor, activatedAt, cancellationToken);
        var reloaded = await _repository.GetByIdAsync(version.Id, cancellationToken) ?? version;

        await _events.AddAsync(new ProcessingEvent
        {
            Actor = actor,
            EventType = previousActive?.Id == version.Id
                ? "extraction-instruction.rolled-back"
                : "extraction-instruction.activated",
            EntityType = "ExtractionInstructionVersion",
            EntityId = version.Id,
            CorrelationId = Guid.NewGuid().ToString(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                versionId = version.Id,
                versionNumber = version.VersionNumber,
                previousActiveVersionId = previousActive?.Id
            })
        }, cancellationToken);

        return reloaded.ToModel();
    }

    private void EnsureAdmin()
    {
        if (!_currentUser.IsAdmin)
            throw new UnauthorizedAccessException("Only admins can manage extraction instructions.");
    }
}
