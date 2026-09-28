using System.Security.Cryptography;
using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Uploads.Commands;

public sealed record UploadItemContentCommand(
    string SessionId,
    string ItemId,
    Guid OccurrenceKey,
    string FileName,
    string MimeType,
    byte[] Content,
    string CorrelationId) : IRequest<UploadItemDto>;

public sealed class UploadItemContentCommandHandler(
    IUploadSessionRepository sessionRepository,
    IApplicationRepository applicationRepository,
    ICurrentUserService currentUser,
    IScoringQueueSignal? queueSignal = null,
    IJobRepository? jobRepository = null,
    IOrganizationRepository? organizations = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<UploadItemContentCommand, UploadItemDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<UploadItemDto> Handle(
        UploadItemContentCommand command,
        CancellationToken cancellationToken)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new UnauthorizedAccessException("Authentication is required.");

        var session = await sessionRepository.GetOwnedAsync(
            command.SessionId, ownerId, includeItems: false, cancellationToken)
            ?? throw new KeyNotFoundException("Upload session was not found.");
        if (jobRepository is not null)
        {
            var job = await jobRepository.GetByIdAsync(session.JobId, cancellationToken)
                ?? throw new KeyNotFoundException("Job was not found.");
            await JobAuthorization.EnsureCanMutateAsync(
                job, currentUser, organizations, cancellationToken);
        }
        var item = await sessionRepository.GetItemAsync(
            command.SessionId, command.ItemId, ownerId, cancellationToken)
            ?? throw new KeyNotFoundException("Upload item was not found.");
        if (!string.Equals(item.OccurrenceKey, command.OccurrenceKey.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("occurrence_key_mismatch");
        if (item.Status.IsTerminal())
        {
            await PublishIfPendingAsync(item, cancellationToken);
            return item.ToDto();
        }
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var priorStatus = item.Status;
        var expectedVersion = item.ConcurrencyVersion;
        item.TransitionTo(UploadItemStatus.Uploading, now);
        item = await sessionRepository.UpdateItemAsync(
            item,
            expectedVersion,
            [StateEvent(item, priorStatus, ownerId, command.CorrelationId, now)],
            cancellationToken);

        var normalizedMime = UploadValidators.NormalizeMimeType(command.FileName, command.MimeType);
        string? validationCode = null;
        string? validationMessage = null;
        if (normalizedMime is null || !string.Equals(normalizedMime, item.MimeType, StringComparison.OrdinalIgnoreCase))
        {
            validationCode = "unsupported_type";
            validationMessage = "The uploaded content type does not match the selected file.";
        }
        else if (command.Content.LongLength != item.RawSizeBytes)
        {
            validationCode = "size_mismatch";
            validationMessage = "The uploaded raw byte length does not match the selected file metadata.";
        }
        else if (command.Content.LongLength > session.MaxIndividualFileBytes)
        {
            validationCode = "size_limit";
            validationMessage = $"The file exceeds the {session.MaxIndividualFileBytes} byte session limit.";
        }

        if (validationCode is not null)
        {
            expectedVersion = item.ConcurrencyVersion;
            priorStatus = item.Status;
            item.TransitionTo(UploadItemStatus.Failed, now, validationCode, validationMessage);
            item = await sessionRepository.UpdateItemAsync(
                item,
                expectedVersion,
                [StateEvent(item, priorStatus, ownerId, command.CorrelationId, now)],
                cancellationToken);
            return item.ToDto();
        }

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(command.Content));
        try
        {
            item = await sessionRepository.CompleteItemAsync(
                command.SessionId,
                command.ItemId,
                ownerId,
                command.OccurrenceKey.ToString(),
                fingerprint,
                Path.GetFileName(command.FileName),
                normalizedMime!,
                command.Content.LongLength,
                Convert.ToBase64String(command.Content),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not (KeyNotFoundException or InvalidOperationException))
        {
            expectedVersion = item.ConcurrencyVersion;
            priorStatus = item.Status;
            new UploadItemLifecycleService().RecordFailure(
                item, System.Net.HttpStatusCode.ServiceUnavailable, now);
            await sessionRepository.UpdateItemAsync(
                item,
                expectedVersion,
                [StateEvent(item, priorStatus, ownerId, command.CorrelationId, now)],
                cancellationToken);
            throw;
        }

        if (item.Status == UploadItemStatus.Succeeded && item.ApplicationId is not null)
            await PublishIfPendingAsync(item, cancellationToken);
        return item.ToDto();
    }

    private async Task PublishIfPendingAsync(
        UploadItem item,
        CancellationToken cancellationToken)
    {
        if (item.Status != UploadItemStatus.Succeeded || item.ApplicationId is null)
            return;
        var application = await applicationRepository.GetByIdAsync(
            item.ApplicationId, cancellationToken);
        if (application is null)
            return;
        if (application.Status == "Uploading")
        {
            await applicationRepository.PublishUploadedAsync(
                [item.ApplicationId], cancellationToken);
            queueSignal?.Pulse();
            return;
        }
        if (application.Status == "Queued")
            queueSignal?.Pulse();
    }

    private static ProcessingEvent StateEvent(
        UploadItem item,
        UploadItemStatus priorStatus,
        string actor,
        string correlationId,
        DateTime timestamp) => new()
        {
            Actor = actor,
            EventType = "upload-item.state-changed",
            EntityType = nameof(UploadItem),
            EntityId = item.Id,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            PayloadJson = JsonSerializer.Serialize(new
            {
                previousStatus = priorStatus.ToWireValue(),
                newStatus = item.Status.ToWireValue(),
                attempt = item.AttemptCount,
                item.OutcomeCode,
            }),
        };
}
