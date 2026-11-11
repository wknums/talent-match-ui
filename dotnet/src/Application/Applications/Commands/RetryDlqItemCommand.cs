using MediatR;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Application.Jobs.Commands;

namespace TalentMatch.Application.Applications.Commands;

public record RetryDlqItemCommand(string ItemId) : IRequest<bool>;

public class RetryDlqItemCommandHandler : IRequestHandler<RetryDlqItemCommand, bool>
{
    private readonly IFailureQueueRepository _dlqRepository;
    private readonly IApplicationRepository _applicationRepository;
    private readonly IJobRepository _jobRepository;
    private readonly ISequentialScoringQueueRepository? _queue;
    private readonly IScoringQueueSignal? _signal;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizations;

    public RetryDlqItemCommandHandler(
        IFailureQueueRepository dlqRepository,
        IApplicationRepository applicationRepository,
        IJobRepository jobRepository,
        ISequentialScoringQueueRepository? queue = null,
        IScoringQueueSignal? signal = null,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizations = null)
    {
        _dlqRepository = dlqRepository;
        _applicationRepository = applicationRepository;
        _jobRepository = jobRepository;
        _queue = queue;
        _signal = signal;
        _currentUser = currentUser;
        _organizations = organizations;
    }

    public async Task<bool> Handle(RetryDlqItemCommand request, CancellationToken cancellationToken)
    {
        var item = await _dlqRepository.GetByIdAsync(request.ItemId, cancellationToken);
        if (item == null) return false;

        if (item.EntityType == "Application")
        {
            var app = await _applicationRepository.GetByIdAsync(item.EntityId, cancellationToken);
            if (app != null)
            {
                var job = await _jobRepository.GetByIdAsync(app.JobId, cancellationToken)
                    ?? throw new InvalidOperationException($"Job '{app.JobId}' not found.");
                await JobAuthorization.EnsureCanMutateAsync(
                    job, _currentUser, _organizations, cancellationToken);

                if (app.TestRunId is null && ProcessJobCommandHandler.ResolveScoringMode() == "sequential")
                {
                    var retried = await (_queue ?? throw new InvalidOperationException("Sequential scoring queue is not registered."))
                        .RetryAsync(request.ItemId, cancellationToken);
                    if (retried)
                        _signal?.Pulse();
                    return retried;
                }
                app.Status = "Queued";
                app.UpdatedAt = DateTime.UtcNow;
                await _applicationRepository.UpdateAsync(app, cancellationToken);
            }
        }

        await _dlqRepository.RemoveAsync(request.ItemId, cancellationToken);
        return true;
    }
}
