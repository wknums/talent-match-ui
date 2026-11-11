using System.Text.Json;
using MediatR;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests;

public sealed record CancelBulkScoringRequestCommand(string RequestId, int ExpectedConcurrencyVersion, string CorrelationId)
    : IRequest<BulkRequestDetailDto>;

// Stops scheduling new work. Waiting files are cancelled at once, importing and converting files stop at their next
// step boundary, and files already handed to scoring finish in the existing pipeline (counts.scoring shows how many).
public sealed class CancelBulkScoringRequestHandler(
    BulkScoringAccess access,
    IBulkScoringRepository repository,
    IProcessingEventRepository events,
    IJobRepository jobs,
    TimeProvider clock) : IRequestHandler<CancelBulkScoringRequestCommand, BulkRequestDetailDto>
{
    private const string FinalizationServiceActor = BulkScoringCycle.FinalizationServiceActor;

    public async Task<BulkRequestDetailDto> Handle(CancelBulkScoringRequestCommand command, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, command.RequestId, mutate: true, ct);
        if (request.Status == BulkRequestStatus.Draft)
            throw BulkApiException.Conflict("invalid_state", "A preview is discarded, not cancelled.");
        if (request.Status == BulkRequestStatus.Completed)
            throw BulkApiException.Conflict("invalid_state", "This request has already finished.");

        // Cancelling twice is not an error: the request is returned as it stands.
        if (!request.CancelRequested)
        {
            var cancelActorId = access.Actor;
            var now = clock.GetUtcNow().UtcDateTime;
            try
            {
                request = await repository.RequestCancelWithAuditAsync(
                    request.Id,
                    command.ExpectedConcurrencyVersion,
                    cancelActorId,
                    now,
                    BulkAuditWriter.User(
                        BulkAuditEventTypes.BulkRequestCancellationRequested,
                        cancelActorId,
                        command.CorrelationId,
                        now,
                        "bulk_request",
                        request.Id,
                        new
                        {
                            state = "cancellation_requested",
                            configurationVersion = command.ExpectedConcurrencyVersion,
                        }),
                    ct);
            }
            catch (InvalidOperationException ex) when (ex.Message == "stale_version")
            {
                throw BulkApiException.Conflict("version_conflict", "The request changed. Reload and try again.");
            }

            await events.AddAsync(new ProcessingEvent
            {
                Actor = cancelActorId,
                EventType = "bulk-scoring-request.cancel-requested",
                EntityType = nameof(BulkScoringRequest),
                EntityId = request.Id,
                CorrelationId = command.CorrelationId,
                Timestamp = now,
                PayloadJson = JsonSerializer.Serialize(new { request.JobId, stillScoring = request.ScoringCount, waitingCancelled = request.CancelledCount }),
            }, ct);

            // Cancelling a request with nothing in flight finishes it on the spot.
            if (request.Status == BulkRequestStatus.Completed)
                await events.AddAsync(new ProcessingEvent
                {
                    Actor = FinalizationServiceActor,
                    EventType = "bulk-scoring-request.completed",
                    EntityType = nameof(BulkScoringRequest),
                    EntityId = request.Id,
                    CorrelationId = command.CorrelationId,
                    Timestamp = now,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        jobId = request.JobId, outcome = request.Outcome?.ToWire(),
                        completed = request.CompletedCount, failed = request.FailedCount, skipped = request.SkippedCount, cancelled = request.CancelledCount,
                    }),
                }, ct);
        }

        return await access.DetailAsync(request, await jobs.GetByIdAsync(request.JobId, ct), null, ct);
    }
}
