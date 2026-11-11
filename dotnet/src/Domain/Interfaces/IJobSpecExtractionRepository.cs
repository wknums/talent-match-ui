using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public interface IJobSpecExtractionRepository
{
    Task AddAsync(JobSpecExtraction extraction, CancellationToken cancellationToken = default);
    Task<JobSpecExtraction?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<JobSpecExtraction?> GetLatestForJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task<JobSpecExtraction?> GetByConfigVersionIdAsync(string jobConfigVersionId, CancellationToken cancellationToken = default);
    Task LinkToJobConfigAsync(string extractionId, string jobId, string jobConfigVersionId, CancellationToken cancellationToken = default);
}
