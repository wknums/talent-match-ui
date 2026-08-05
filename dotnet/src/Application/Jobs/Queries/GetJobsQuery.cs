using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Queries;

public record GetJobsQuery : IRequest<IReadOnlyList<Job>>;

public class GetJobsQueryHandler : IRequestHandler<GetJobsQuery, IReadOnlyList<Job>>
{
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService _currentUser;

    public GetJobsQueryHandler(IJobRepository jobRepository, ICurrentUserService currentUser)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<Job>> Handle(GetJobsQuery request, CancellationToken cancellationToken)
    {
        var authorizationState = await _currentUser.GetAuthorizationStateAsync(cancellationToken);
        if (authorizationState is null)
        {
            if (_currentUser.IsAdmin)
                return await _jobRepository.GetAllAsync(cancellationToken);

            return await _jobRepository.GetByDepartmentAsync(
                _currentUser.Department ?? "", cancellationToken);
        }

        var hasGlobalAdmin = authorizationState.Assignments.Any(assignment =>
            assignment.Status == "active"
            && assignment.Role == "admin"
            && assignment.OrganizationId is null
            && assignment.DepartmentId is null);
        var candidates = hasGlobalAdmin
            ? await _jobRepository.GetAllAsync(cancellationToken)
            : await _jobRepository.GetByDepartmentAsync(
                _currentUser.Department ?? "", cancellationToken);

        return candidates.Where(job => JobAuthorization.CanRead(authorizationState, job)).ToArray();
    }
}
