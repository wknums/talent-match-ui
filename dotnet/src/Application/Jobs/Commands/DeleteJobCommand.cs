using MediatR;
using Microsoft.Extensions.Logging;
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
    private readonly IBulkScoringRepository? _bulkRepository;
    private readonly IBulkContentStore? _bulkContent;
    private readonly ILogger<DeleteJobCommandHandler>? _logger;

    public DeleteJobCommandHandler(
        IJobRepository jobRepository,
        ICurrentUserService currentUser,
        IOrganizationRepository? organizationRepository = null,
        IBulkScoringRepository? bulkRepository = null,
        IBulkContentStore? bulkContent = null,
        ILogger<DeleteJobCommandHandler>? logger = null)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
        _bulkRepository = bulkRepository;
        _bulkContent = bulkContent;
        _logger = logger;
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

        // The request rows go with the job; the staged and retained files they point to live outside the database.
        var bulkRequestIds = _bulkRepository is null
            ? []
            : await _bulkRepository.ListRequestIdsForJobAsync(request.JobId, cancellationToken);

        var deletingActorId = _currentUser.UserId;
        if (string.IsNullOrWhiteSpace(deletingActorId))
            deletingActorId = authorizationState?.Claims.ObjectId ?? "administrator";
        var correlationId = Guid.NewGuid().ToString();
        var utcNow = DateTime.UtcNow;
        if (_bulkRepository is not null)
            await _bulkRepository.BeginJobCleanupAsync(
                request.JobId,
                deletingActorId,
                correlationId,
                utcNow,
                cancellationToken);

        var cleanupFailed = false;
        if (_bulkContent is not null)
            foreach (var bulkRequestId in bulkRequestIds)
            {
                try
                {
                    await _bulkContent.DeleteByRequestAsync(bulkRequestId, cancellationToken);
                }
                catch (Exception ex)
                {
                    cleanupFailed = true;
                    _logger?.LogError(ex, "Files of bulk request {RequestId} could not be deleted with job {JobId}", bulkRequestId, request.JobId);
                }
            }

        if (cleanupFailed)
            return false;

        var deletedAtomically = _bulkRepository is not null
            && await _bulkRepository.CompleteJobCleanupAsync(
                request.JobId,
                deletingActorId,
                correlationId,
                utcNow,
                cancellationToken);
        if (!deletedAtomically)
            await _jobRepository.DeleteAsync(request.JobId, cancellationToken);

        return true;
    }
}
