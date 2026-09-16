using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Application.JobExtraction.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.ExtractionInstructions.Commands;

public sealed record ValidateExtractionInstructionCommand(
    string VersionId,
    string FileName,
    string Content,
    string MimeType)
    : IRequest<JobSpecExtractionExecutionResult>;

public sealed class ValidateExtractionInstructionCommandHandler : IRequestHandler<ValidateExtractionInstructionCommand, JobSpecExtractionExecutionResult>
{
    private readonly IExtractionInstructionRepository _repository;
    private readonly IExtractionInstructionValidationRunner _runner;
    private readonly ICurrentUserService _currentUser;
    private readonly IProcessingEventRepository _events;

    public ValidateExtractionInstructionCommandHandler(
        IExtractionInstructionRepository repository,
        IExtractionInstructionValidationRunner runner,
        ICurrentUserService currentUser,
        IProcessingEventRepository events)
    {
        _repository = repository;
        _runner = runner;
        _currentUser = currentUser;
        _events = events;
    }

    public async Task<JobSpecExtractionExecutionResult> Handle(ValidateExtractionInstructionCommand request, CancellationToken cancellationToken)
    {
        EnsureAdmin();
        var version = await _repository.GetByIdAsync(request.VersionId, cancellationToken)
            ?? throw new InvalidOperationException($"Instruction version '{request.VersionId}' was not found.");

        var result = await _runner.ValidateAsync(
            version.Id,
            new ExtractDocumentRequestModel(request.FileName, request.Content, request.MimeType),
            cancellationToken);

        version.ValidationStatus = result.ValidationStatus;
        version.ValidationFindingsJson = JsonSerializer.Serialize(result.ValidationFindings);
        version.ValidatedAt = DateTime.UtcNow;
        version.ValidatedBy = _currentUser.UserId ?? _currentUser.Username ?? "unknown";
        await _repository.UpdateAsync(version, cancellationToken);

        await _events.AddAsync(new ProcessingEvent
        {
            Actor = version.ValidatedBy,
            EventType = result.IsValid
                ? "extraction-instruction.validated"
                : "extraction-instruction.validation-failed",
            EntityType = "ExtractionInstructionVersion",
            EntityId = version.Id,
            CorrelationId = Guid.NewGuid().ToString(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                versionId = version.Id,
                extractionId = result.ExtractionId,
                validationStatus = result.ValidationStatus,
                findingCodes = result.ValidationFindings.Select(finding => finding.Code).Distinct().ToArray()
            })
        }, cancellationToken);

        return result;
    }

    private void EnsureAdmin()
    {
        if (!_currentUser.IsAdmin)
            throw new UnauthorizedAccessException("Only admins can manage extraction instructions.");
    }
}
