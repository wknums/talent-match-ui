using MediatR;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record CancelJobScoringCommand(string JobId) : IRequest<CancelJobScoringResult>;

public record CancelJobScoringResult(int AffectedBatches);

public class CancelJobScoringCommandHandler : IRequestHandler<CancelJobScoringCommand, CancelJobScoringResult>
{
    private readonly IScoringBatchRepository _batchRepo;
    public CancelJobScoringCommandHandler(IScoringBatchRepository batchRepo) => _batchRepo = batchRepo;

    public async Task<CancelJobScoringResult> Handle(CancelJobScoringCommand request, CancellationToken ct)
    {
        await _batchRepo.RequestCancelProgressAsync(request.JobId, ct);
        var affected = await _batchRepo.RequestCancelByJobAsync(request.JobId, ct);
        return new CancelJobScoringResult(affected);
    }
}
