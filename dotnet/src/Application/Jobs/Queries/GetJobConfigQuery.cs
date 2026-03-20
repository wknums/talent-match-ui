using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Queries;

public record GetJobConfigQuery(string JobId) : IRequest<JobConfigVersion?>;

public class GetJobConfigQueryHandler : IRequestHandler<GetJobConfigQuery, JobConfigVersion?>
{
    private readonly IJobRepository _jobRepository;

    public GetJobConfigQueryHandler(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public async Task<JobConfigVersion?> Handle(GetJobConfigQuery request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken);
        if (job is null || job.CurrentConfigVersionId is null)
            return null;

        return job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId);
    }
}
