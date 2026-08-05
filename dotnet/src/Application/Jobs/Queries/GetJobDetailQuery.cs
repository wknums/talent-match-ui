using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Queries;

public record GetJobDetailQuery(string JobId) : IRequest<Job?>;

public class GetJobDetailQueryHandler : IRequestHandler<GetJobDetailQuery, Job?>
{
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;

    public GetJobDetailQueryHandler(
        IJobRepository jobRepository,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizationRepository = null)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
    }

    public async Task<Job?> Handle(GetJobDetailQuery request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken);
        if (job is not null)
            await JobAuthorization.EnsureCanReadAsync(
                job, _currentUser, _organizationRepository, cancellationToken);

        return job;
    }
}
