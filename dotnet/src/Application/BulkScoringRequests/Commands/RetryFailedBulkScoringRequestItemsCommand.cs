using System.Text.Json;
using MediatR;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests;

public sealed record RetryFailedBulkScoringRequestItemsCommand(
    string RequestId,
    string CorrelationId,
    string? ConnectionId = null) : IRequest<BulkRequestDetailDto>;

// Starts a new request over the retryable files of a finished one; the finished request is left as it was.
public sealed class RetryFailedBulkScoringRequestItemsHandler(
    BulkScoringAccess access,
    IBulkScoringRepository repository,
    ISharePointSource source,
    IProcessingEventRepository events,
    IJobRepository jobs,
    TimeProvider clock,
    IRequestAuthorizationStore? authorizationStore = null) : IRequestHandler<RetryFailedBulkScoringRequestItemsCommand, BulkRequestDetailDto>
{
    public async Task<BulkRequestDetailDto> Handle(RetryFailedBulkScoringRequestItemsCommand command, CancellationToken ct)
    {
        var original = await BulkRequestLoader.LoadAsync(access, repository, command.RequestId, mutate: true, ct);
        if (original.Status != BulkRequestStatus.Completed)
            throw BulkApiException.Conflict("invalid_state", "Only a finished request can be retried.");

        var connectionId = command.ConnectionId;
        if (string.IsNullOrWhiteSpace(connectionId))
            throw BulkApiException.AuthorizationRequired();

        // Retry reads must use the newly connected source identity, never the original request cache.
        var context = access.InteractiveContext(connectionId);
        var folder = await access.ResolveAsync(original.SourceWebUrl, context, ct);
        if (folder.Kind != original.SourceKind
                || !string.Equals(folder.TenantId, original.SourceTenantId, StringComparison.Ordinal)
                || !string.Equals(folder.DriveId, original.SourceDriveId, StringComparison.Ordinal)
                || !string.Equals(folder.ItemId, original.SourceFolderItemId, StringComparison.Ordinal))
            throw BulkApiException.AccessDenied();

        var candidates = await repository.GetRetryableItemsAsync(original.Id, ct);
        var retryable = new List<BulkScoringItem>(candidates.Count);
        foreach (var item in candidates)
            if (await CanRetryAsync(item, folder, context, ct)) retryable.Add(item);

        if (retryable.Count == 0)
            throw BulkApiException.Invalid(
                "No files from this request are eligible for bulk retry.",
                "no_retryable_bulk_files");

        var job = (await jobs.GetByIdAsync(original.JobId, ct))!;
        var now = clock.GetUtcNow().UtcDateTime;
        var retry = BulkScoringRequest.CreateRetry(
            original, access.Actor, job.CurrentConfigVersionId, command.CorrelationId, now);
        retry.ConnectionId = connectionId;

        if (authorizationStore is null) throw BulkApiException.Unavailable();
        BulkRequestAuthorization authorization;
        try
        {
            authorization = await authorizationStore.TransferToRequestAsync(
                retry.Id,
                connectionId!,
                access.Actor,
                retry.JobId,
                ct);
        }
        catch (AuthorizationRequiredException) { throw BulkApiException.AuthorizationRequired(); }

        if (string.IsNullOrWhiteSpace(authorization.SourceAccountBindingHash)
            || !authorization.IsBoundTo(
                retry.Id,
                access.Actor,
                folder.TenantId,
                authorization.SourceAccountBindingHash,
                folder.DriveId,
                folder.ItemId))
        {
            await authorizationStore.RollbackTransferAsync(authorization, ct);
            throw BulkApiException.AuthorizationRequired();
        }

        retry.SourceTenantId = authorization.SourceTenantId;
        retry.SourceAccountBindingHash = authorization.SourceAccountBindingHash;
        retry.SourceDriveId = authorization.SourceDriveId;
        retry.SourceFolderItemId = authorization.SourceFolderItemId;

        var retryItems = retryable.Select(i => i.CreateRetry(now)).ToArray();
        var typedAudits = new List<BulkAuditEvent>
        {
            BulkAuditWriter.User(
                BulkAuditEventTypes.BulkRequestRetryCreated,
                access.Actor,
                command.CorrelationId,
                now,
                "bulk_request",
                retry.Id,
                new { state = "active", itemCount = retryItems.Length, retryOfRequestId = original.Id }),
        };
        if (authorization is not null)
            typedAudits.Add(BulkAuditWriter.User(
                BulkAuditEventTypes.RequestAuthorizationEstablished,
                access.Actor,
                command.CorrelationId,
                now,
                "request_authorization",
                retry.Id,
                new { state = authorization.Status.ToWire() }));
        typedAudits.AddRange(retryItems.Select(item => BulkAuditWriter.User(
            BulkAuditEventTypes.BulkRetryItemCreated,
            access.Actor,
            command.CorrelationId,
            now,
            "bulk_item",
            item.Id,
            new { state = item.Status.ToWire(), byteCount = item.SizeBytes, retryOfItemId = item.RetryOfItemId })));

        BulkScoringRequest created;
        try
        {
            created = await repository.CreateRetryRequestWithAuditAsync(
                retry,
                retryItems,
                authorization,
                typedAudits,
                ct);
        }
        catch
        {
            if (authorization is not null && authorizationStore is not null)
                await authorizationStore.RollbackTransferAsync(authorization, CancellationToken.None);
            throw;
        }

        await events.AddAsync(new ProcessingEvent
        {
            Actor = access.Actor,
            EventType = "bulk-scoring-request.retry-created",
            EntityType = nameof(BulkScoringRequest),
            EntityId = created.Id,
            CorrelationId = command.CorrelationId,
            Timestamp = now,
            PayloadJson = JsonSerializer.Serialize(new { created.JobId, retryOfRequestId = original.Id, items = created.DiscoveredCount }),
        }, ct);

        if (authorization is not null)
            await events.AddAsync(new ProcessingEvent
            {
                Actor = access.Actor,
                EventType = "bulk-scoring-authorization.created",
                EntityType = "BulkRequestAuthorization",
                EntityId = created.Id,
                CorrelationId = command.CorrelationId,
                Timestamp = now,
                PayloadJson = JsonSerializer.Serialize(new { created.JobId, reasonCode = (string?)null, itemsFailed = 0 }),
            }, ct);

        return await access.DetailAsync(created, job, null, ct);
    }

    // A file that is gone or no longer readable is retried only when it resolves again for this recruiter.
    private async Task<bool> CanRetryAsync(
        BulkScoringItem item,
        SourceFolder folder,
        SourceCallContext context,
        CancellationToken ct)
    {
        if (item.ReasonCode is not { } reason || !BulkStateMachine.RetryRequiresResolution(reason)) return true;
        try
        {
            return await source.GetItemMetadataAsync(folder, item.SourceDriveItemId, context, ct) is not null;
        }
        catch (SourceAccessDeniedException) { return false; }
        catch (SourceNotFoundException) { return false; }
    }
}
