using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Queries;

public record GetJobConfigQuery(string JobId) : IRequest<JobConfigVersion?>;

public class GetJobConfigQueryHandler : IRequestHandler<GetJobConfigQuery, JobConfigVersion?>
{
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;

    public GetJobConfigQueryHandler(
        IJobRepository jobRepository,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizationRepository = null)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
    }

    public async Task<JobConfigVersion?> Handle(GetJobConfigQuery request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken);
        if (job is null || job.CurrentConfigVersionId is null)
            return null;

        await JobAuthorization.EnsureCanReadAsync(
            job, _currentUser, _organizationRepository, cancellationToken);

        return job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId);
    }
}
