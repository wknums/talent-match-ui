using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record DeleteJobCommand(string JobId) : IRequest<bool>;

public class DeleteJobCommandHandler : IRequestHandler<DeleteJobCommand, bool>
{
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService _currentUser;

    public DeleteJobCommandHandler(IJobRepository jobRepository, ICurrentUserService currentUser)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
    }

    public async Task<bool> Handle(DeleteJobCommand request, CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can delete jobs.");

        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken);
        if (job == null) return false;

        await _jobRepository.DeleteAsync(request.JobId, cancellationToken);
        return true;
    }
}
