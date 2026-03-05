using MediatR;
using TalentMatch.Application.Common.Interfaces;
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
        if (_currentUser.IsAdmin)
            return await _jobRepository.GetAllAsync(cancellationToken);
        
        return await _jobRepository.GetByDepartmentAsync(_currentUser.Department ?? "", cancellationToken);
    }
}
