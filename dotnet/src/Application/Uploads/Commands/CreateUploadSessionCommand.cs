using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Application.Prompts.Services;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Uploads.Commands;

public sealed record CreateUploadSessionCommand(
    string JobId,
    CreateUploadSessionRequest Request,
    string CorrelationId) : IRequest<UploadSessionDetailDto>;

public sealed class CreateUploadSessionCommandHandler(
    IUploadSettingsRepository settingsRepository,
    IUploadSessionRepository sessionRepository,
    IJobRepository jobRepository,
    ICurrentUserService currentUser,
    IOrganizationRepository? organizations = null,
    IScoringPromptRepository? prompts = null,
    IPromptProfileGuard? profileGuard = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<CreateUploadSessionCommand, UploadSessionDetailDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<UploadSessionDetailDto> Handle(
        CreateUploadSessionCommand command,
        CancellationToken cancellationToken)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new UnauthorizedAccessException("Authentication is required.");

        var job = await jobRepository.GetByIdAsync(command.JobId, cancellationToken)
            ?? throw new KeyNotFoundException("Job was not found.");
        await JobAuthorization.EnsureCanMutateAsync(job, currentUser, organizations, cancellationToken);
        if (prompts is not null && profileGuard is not null)
        {
            var productionPrompt = await prompts.GetProductionApprovedForJobAsync(
                command.JobId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A production-approved prompt is required before applications can be queued.");
            await profileGuard.EnsureProductionReadyAsync(productionPrompt, cancellationToken);
        }

        async Task<UploadSessionDetailDto> CreateWithLatestSettingsAsync()
        {
            var persistedSettings = await settingsRepository.GetPersistedAsync(cancellationToken);
            var settings = persistedSettings ?? UploadSettings.Defaults();
            var requestItems = command.Request.Items ?? [];
            var duplicateOccurrenceKeys = requestItems
                .GroupBy(x => x.OccurrenceKey)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToHashSet();
            var duplicateOrdinals = requestItems
                .GroupBy(x => x.Ordinal)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToHashSet();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var items = new List<UploadItem>(requestItems.Count);
            var validationErrors = new Dictionary<string, string[]>();

            foreach (var requested in requestItems.OrderBy(x => x.Ordinal))
            {
                var errors = UploadValidators.ValidateItem(requested, settings.MaxIndividualFileBytes).ToList();
                if (duplicateOccurrenceKeys.Contains(requested.OccurrenceKey))
                    errors.Add("Occurrence keys must be unique within a session.");
                if (duplicateOrdinals.Contains(requested.Ordinal))
                    errors.Add("Ordinals must be unique within a session.");
                var normalizedMime = UploadValidators.NormalizeMimeType(requested.FileName, requested.MimeType)
                    ?? requested.MimeType;
                var item = new UploadItem
                {
                    SessionId = string.Empty,
                    OccurrenceKey = requested.OccurrenceKey.ToString(),
                    Ordinal = requested.Ordinal,
                    FileName = SafeDisplayFileName(requested.FileName),
                    MimeType = normalizedMime,
                    RawSizeBytes = requested.RawSizeBytes,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                if (errors.Count > 0)
                {
                    item.Status = UploadItemStatus.Failed;
                    item.OutcomeCode = errors.Any(x => x.Contains("byte limit", StringComparison.OrdinalIgnoreCase))
                        ? "size_limit"
                        : "validation_failed";
                    item.OutcomeMessage = string.Join(" ", errors);
                    item.CompletedAt = now;
                    validationErrors[$"items[{requested.Ordinal}]"] = errors.ToArray();
                }
                items.Add(item);
            }

            if (items.Count == 0 || items.All(x => x.Status.IsTerminal()))
                throw new UploadValidationException(validationErrors.Count == 0
                    ? new Dictionary<string, string[]> { ["items"] = ["Select at least one eligible file."] }
                    : validationErrors);

            var session = new UploadSession
            {
                JobId = command.JobId,
                OwnerActorId = ownerId,
                OwnerDisplayName = currentUser.Username,
                AllowDuplicates = command.Request.AllowDuplicates,
                FileConcurrency = settings.FileConcurrency,
                MaxIndividualFileBytes = settings.MaxIndividualFileBytes,
                MaxInFlightBytes = settings.MaxInFlightBytes,
                CorrelationId = command.CorrelationId,
                LastHeartbeatAt = now,
                CreatedAt = now,
            };
            foreach (var item in items)
                item.SessionId = session.Id;
            session.ApplyAggregates(items, now);

            var createdEvent = new ProcessingEvent
            {
                Actor = ownerId,
                EventType = "upload-session.created",
                EntityType = nameof(UploadSession),
                EntityId = session.Id,
                CorrelationId = command.CorrelationId,
                Timestamp = now,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    session.JobId,
                    itemCount = items.Count,
                    session.AllowDuplicates,
                    limits = new { session.FileConcurrency, session.MaxIndividualFileBytes, session.MaxInFlightBytes },
                }),
            };
            var created = await sessionRepository.CreateAsync(
                session,
                items,
                createdEvent,
                persistedSettings?.ConcurrencyVersion ?? 0,
                cancellationToken);
            return created.ToDetailDto();
        }

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                return await CreateWithLatestSettingsAsync();
            }
            catch (InvalidOperationException ex) when (
                ex.Message == "settings_stale" && attempt < 4)
            {
                // Revalidate the entire request against the settings that won the commit race.
            }
        }

        throw new InvalidOperationException(
            "Upload settings changed repeatedly while the session was being created.");
    }

    private static string SafeDisplayFileName(string fileName)
    {
        var displayName = Path.GetFileName(fileName);
        return displayName.Length <= 500 ? displayName : displayName[..500];
    }
}
