using TalentMatch.Application.JobExtraction.Models;

namespace TalentMatch.Application.JobExtraction.Services;

public interface IExtractionInstructionValidationRunner
{
    Task<JobSpecExtractionExecutionResult> ValidateAsync(
        string instructionVersionId,
        ExtractDocumentRequestModel request,
        CancellationToken cancellationToken = default);
}
