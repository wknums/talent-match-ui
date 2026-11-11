using MediatR;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests;

public sealed record AcknowledgeBulkScoringRequestCommand(
    string RequestId,
    string? CorrelationId = null) : IRequest;

// Authorization is intentionally checked as "not found", then repeated by the repository's
// conditional update so ownership cannot change between the read and the atomic write.
public sealed class AcknowledgeBulkScoringRequestHandler(
    BulkScoringAccess access,
    IBulkScoringRepository repository,
    TimeProvider clock)
    : IRequestHandler<AcknowledgeBulkScoringRequestCommand>
{
    public async Task Handle(AcknowledgeBulkScoringRequestCommand command, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, command.RequestId, mutate: false, ct);
        if (!string.Equals(request.OwnerActorId, access.Actor, StringComparison.Ordinal))
            throw BulkApiException.NotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        await repository.AcknowledgeWithAuditAsync(
            request.Id,
            now,
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkNotificationAcknowledged,
                access.Actor,
                command.CorrelationId ?? request.CorrelationId,
                now,
                "bulk_request",
                request.Id,
                new { state = "acknowledged", configurationVersion = request.ConcurrencyVersion }),
            ct);
    }
}
