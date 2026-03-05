namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IJobRepository
{
    Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Job>> GetByDepartmentAsync(string department, CancellationToken cancellationToken = default);
    Task<Job?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(Job job, CancellationToken cancellationToken = default);
    Task UpdateAsync(Job job, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task AddConfigVersionAsync(JobConfigVersion version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobConfigVersion>> GetConfigVersionsAsync(string jobId, CancellationToken cancellationToken = default);
}
