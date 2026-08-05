using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record CancelJobScoringCommand(string JobId) : IRequest<CancelJobScoringResult>;

public record CancelJobScoringResult(int AffectedBatches);

public class CancelJobScoringCommandHandler : IRequestHandler<CancelJobScoringCommand, CancelJobScoringResult>
{
    private readonly IScoringBatchRepository _batchRepo;
    private readonly IJobRepository? _jobRepository;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;

    public CancelJobScoringCommandHandler(
        IScoringBatchRepository batchRepo,
        IJobRepository? jobRepository = null,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizationRepository = null)
    {
        _batchRepo = batchRepo;
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
    }

    public async Task<CancelJobScoringResult> Handle(CancelJobScoringCommand request, CancellationToken ct)
    {
        if (_jobRepository is not null)
        {
            var job = await _jobRepository.GetByIdAsync(request.JobId, ct)
                ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");
            await JobAuthorization.EnsureCanMutateAsync(
                job, _currentUser, _organizationRepository, ct);
        }

        await _batchRepo.RequestCancelProgressAsync(request.JobId, ct);
        var affected = await _batchRepo.RequestCancelByJobAsync(request.JobId, ct);
        return new CancelJobScoringResult(affected);
    }
}
