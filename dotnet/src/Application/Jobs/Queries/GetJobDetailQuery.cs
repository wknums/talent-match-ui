using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Queries;

public record GetJobDetailQuery(string JobId) : IRequest<Job?>;

public class GetJobDetailQueryHandler : IRequestHandler<GetJobDetailQuery, Job?>
{
    private readonly IJobRepository _jobRepository;

    public GetJobDetailQueryHandler(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public async Task<Job?> Handle(GetJobDetailQuery request, CancellationToken cancellationToken)
    {
        return await _jobRepository.GetByIdAsync(request.JobId, cancellationToken);
    }
}
