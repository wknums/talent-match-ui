using MediatR;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Application.JobExtraction.Services;

namespace TalentMatch.Application.JobExtraction.Commands;

public sealed record ExtractJobSpecCommand(
    string FileName,
    string Content,
    string MimeType,
    string Purpose = "job_creation",
    string? InstructionVersionId = null)
    : IRequest<JobSpecExtractionExecutionResult>;

public sealed class ExtractJobSpecCommandHandler : IRequestHandler<ExtractJobSpecCommand, JobSpecExtractionExecutionResult>
{
    private readonly JobSpecExtractionOrchestrator _orchestrator;

    public ExtractJobSpecCommandHandler(JobSpecExtractionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public Task<JobSpecExtractionExecutionResult> Handle(
        ExtractJobSpecCommand request,
        CancellationToken cancellationToken)
        => _orchestrator.ExecuteAsync(
            new ExtractDocumentRequestModel(request.FileName, request.Content, request.MimeType),
            request.Purpose,
            request.InstructionVersionId,
            cancellationToken);
}
