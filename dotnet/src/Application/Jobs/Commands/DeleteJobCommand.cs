using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record DeleteJobCommand(string JobId) : IRequest<bool>;

public class DeleteJobCommandHandler : IRequestHandler<DeleteJobCommand, bool>
{
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;

    public DeleteJobCommandHandler(
        IJobRepository jobRepository,
        ICurrentUserService currentUser,
        IOrganizationRepository? organizationRepository = null)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
    }

    public async Task<bool> Handle(DeleteJobCommand request, CancellationToken cancellationToken)
    {
        var authorizationState = await _currentUser.GetAuthorizationStateAsync(cancellationToken);
        if (authorizationState is null && !_currentUser.IsAdmin)
            throw new UnauthorizedAccessException("Only administrators can delete jobs.");

        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken);
        if (job == null) return false;

        if (authorizationState is not null)
        {
            if (!JobAuthorization.CanMutate(authorizationState, job))
                throw new UnauthorizedAccessException("The current user cannot delete this job.");
            await JobAuthorization.EnsureValidScopeAsync(
                job, authorizationState, _organizationRepository, cancellationToken);
        }

        await _jobRepository.DeleteAsync(request.JobId, cancellationToken);
        return true;
    }
}
