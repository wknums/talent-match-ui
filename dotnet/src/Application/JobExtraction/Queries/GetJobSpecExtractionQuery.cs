using System.Text.Json;
using MediatR;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.JobExtraction.Queries;

public sealed record GetJobSpecExtractionQuery(string JobId) : IRequest<JobSpecExtractionRecordModel?>;

public sealed class GetJobSpecExtractionQueryHandler : IRequestHandler<GetJobSpecExtractionQuery, JobSpecExtractionRecordModel?>
{
    private readonly IJobSpecExtractionRepository _repository;

    public GetJobSpecExtractionQueryHandler(IJobSpecExtractionRepository repository)
    {
        _repository = repository;
    }

    public async Task<JobSpecExtractionRecordModel?> Handle(GetJobSpecExtractionQuery request, CancellationToken cancellationToken)
    {
        var extraction = await _repository.GetLatestForJobAsync(request.JobId, cancellationToken);
        if (extraction is null)
            return null;

        var findings = JsonSerializer.Deserialize<List<ExtractionValidationFinding>>(
            string.IsNullOrWhiteSpace(extraction.ValidationFindingsJson) ? "[]" : extraction.ValidationFindingsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        return new JobSpecExtractionRecordModel(
            extraction.Id,
            extraction.Purpose,
            extraction.InstructionVersionId,
            extraction.ProtectedContractVersion,
            extraction.SourceFileName,
            extraction.SourceMimeType,
            extraction.SourceSha256,
            extraction.RawResponse,
            extraction.NormalizedResponseJson,
            extraction.ValidationStatus,
            findings,
            extraction.JobId,
            extraction.JobConfigVersionId,
            extraction.CreatedAt,
            extraction.CreatedBy,
            extraction.CompletedAt,
            extraction.CorrelationId);
    }
}
