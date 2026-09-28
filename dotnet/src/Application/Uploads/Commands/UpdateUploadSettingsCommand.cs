using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Uploads.Commands;

public sealed record UpdateUploadSettingsCommand(
    UpdateUploadSettingsRequest Request,
    string CorrelationId) : IRequest<UploadSettingsDto>;

public sealed class UpdateUploadSettingsCommandHandler(
    IUploadSettingsRepository repository,
    IProcessingEventRepository events,
    ICurrentUserService currentUser,
    TimeProvider? timeProvider = null)
    : IRequestHandler<UpdateUploadSettingsCommand, UploadSettingsDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<UploadSettingsDto> Handle(
        UpdateUploadSettingsCommand command,
        CancellationToken cancellationToken)
    {
        var errors = UploadSettings.Validate(
            command.Request.FileConcurrency,
            command.Request.MaxIndividualFileBytes,
            command.Request.MaxInFlightBytes);
        if (errors.Count > 0)
            throw new UploadValidationException(errors);
        if (!currentUser.IsAdmin || string.IsNullOrWhiteSpace(currentUser.UserId))
            throw new UnauthorizedAccessException("Global administrator access is required.");

        var previous = await repository.GetPersistedAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var actor = currentUser.UserId;
        var settings = new UploadSettings
        {
            FileConcurrency = command.Request.FileConcurrency,
            MaxIndividualFileBytes = command.Request.MaxIndividualFileBytes,
            MaxInFlightBytes = command.Request.MaxInFlightBytes,
            CreatedAt = previous?.CreatedAt ?? now,
            CreatedBy = previous?.CreatedBy ?? actor,
            UpdatedAt = now,
            UpdatedBy = actor,
        };
        var saved = await repository.SaveAsync(
            settings, command.Request.ExpectedConcurrencyVersion, cancellationToken);
        await events.AddAsync(new ProcessingEvent
        {
            Actor = actor,
            EventType = "upload-settings.updated",
            EntityType = nameof(UploadSettings),
            EntityId = UploadSettings.SingletonId,
            CorrelationId = command.CorrelationId,
            Timestamp = now,
            PayloadJson = JsonSerializer.Serialize(new
            {
                previous = previous is null ? null : new
                {
                    previous.FileConcurrency,
                    previous.MaxIndividualFileBytes,
                    previous.MaxInFlightBytes,
                    previous.ConcurrencyVersion,
                },
                current = new
                {
                    saved.FileConcurrency,
                    saved.MaxIndividualFileBytes,
                    saved.MaxInFlightBytes,
                    saved.ConcurrencyVersion,
                },
            }),
        }, cancellationToken);
        return new(
            saved.FileConcurrency,
            saved.MaxIndividualFileBytes,
            saved.MaxInFlightBytes,
            saved.ConcurrencyVersion,
            true,
            saved.UpdatedAt,
            saved.UpdatedBy);
    }
}
