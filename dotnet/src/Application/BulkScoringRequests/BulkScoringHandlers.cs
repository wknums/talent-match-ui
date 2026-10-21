using System.Text.Json;
using MediatR;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests;

public sealed record BulkConfirmResult(BulkRequestDetailDto Request, bool Created);

public sealed record BulkItemDto(
    string ItemId, string FileName, string Path, long SizeBytes, string Status, string? ReasonCode, string? ReasonMessage,
    int Attempt, string? ApplicationId, string? ScoredVersion, bool HasRetainedOriginal, bool ExcludedWhenRasterized,
    string MimeType, int? PageCount, long? ConvertedSizeBytes, string? ScoringConfigVersionId, DateTime UpdatedAt);

public sealed record BulkItemPageDto(IReadOnlyList<BulkItemDto> Items, int Page, int PageSize, int Total);

public sealed record GetBulkScoringCapabilitiesQuery : IRequest<BulkCapabilitiesDto>;
public sealed record GetBulkScoringSettingsQuery : IRequest<BulkSettingsDto>;
public sealed record UpdateBulkScoringSettingsCommand(UpdateBulkSettingsRequest Request, string CorrelationId) : IRequest<BulkSettingsDto>;
public sealed record PreviewBulkScoringRequestCommand(
    string JobId,
    string FolderUrl,
    string? CorrelationId = null) : IRequest<BulkPreviewResponseDto>;
public sealed record PreviewSourceConnectionCommand(
    string ConnectionId,
    string? CorrelationId = null) : IRequest<SourceConnectionPreviewResponse>;
public sealed record DiscoverMoreBulkScoringRequestCommand(
    string RequestId,
    string? CorrelationId = null) : IRequest<BulkDiscoveryStepDto>;
public sealed record ConfirmBulkScoringRequestCommand(string RequestId, BulkConfirmRequest Request, string CorrelationId) : IRequest<BulkConfirmResult>;
public sealed record AbandonBulkScoringRequestDraftCommand(
    string RequestId,
    string? CorrelationId = null) : IRequest;
public sealed record GetBulkScoringRequestQuery(string RequestId) : IRequest<BulkRequestDetailDto>;
public sealed record GetBulkScoringItemsQuery(string RequestId, int Page, int PageSize, string? Status, string? NameContains) : IRequest<BulkItemPageDto>;

public sealed class GetBulkScoringCapabilitiesHandler(BulkScoringOptions options, ISharePointSource source, BulkScoringAccess access)
    : IRequestHandler<GetBulkScoringCapabilitiesQuery, BulkCapabilitiesDto>
{
    public async Task<BulkCapabilitiesDto> Handle(GetBulkScoringCapabilitiesQuery request, CancellationToken ct)
    {
        var extensions = new List<string> { ".pdf", ".png", ".jpg", ".jpeg", ".txt" };
        foreach (var e in new[] { ".docx", ".md" }) if (source.CanConvertToPdf(e) || !options.Enabled) extensions.Add(e);
        // TXT is listed because it is eligible; it bypasses conversion and is scored as the original.
        var limits = options.Enabled ? await access.SnapshotLimitsAsync(ct) : BulkRequestLimits.Snapshot(options, UploadSettings.DefaultMaxIndividualFileBytes, BulkImportSettings.DefaultMaxFilesPerRequest);
        return new BulkCapabilitiesDto(
            options.Enabled, options.Mode.ToString().ToLowerInvariant(), "delegated",
            options.SupportedSources.Select(s => s.ToWire()).ToArray(),
            Array.Empty<string>(),
            extensions, BulkScoringAccess.LimitsDto(limits));
    }
}

public sealed class GetBulkScoringSettingsHandler(IBulkImportSettingsRepository repository, ICurrentUserService currentUser)
    : IRequestHandler<GetBulkScoringSettingsQuery, BulkSettingsDto>
{
    public async Task<BulkSettingsDto> Handle(GetBulkScoringSettingsQuery request, CancellationToken ct)
    {
        if (!currentUser.IsAdmin) throw new UnauthorizedAccessException("Global administrator access is required.");
        var saved = await repository.GetPersistedAsync(ct);
        return saved is null
            ? new(BulkImportSettings.DefaultMaxFilesPerRequest, 0, null, null)
            : new(saved.MaxFilesPerRequest, saved.ConcurrencyVersion, saved.UpdatedAt, saved.UpdatedByActorId);
    }
}

public sealed class UpdateBulkScoringSettingsHandler(
    IBulkImportSettingsRepository repository,
    ICurrentUserService currentUser,
    TimeProvider clock) : IRequestHandler<UpdateBulkScoringSettingsCommand, BulkSettingsDto>
{
    public async Task<BulkSettingsDto> Handle(UpdateBulkScoringSettingsCommand command, CancellationToken ct)
    {
        var errors = BulkImportSettings.Validate(command.Request.MaxFilesPerRequest);
        if (errors.Count > 0) throw new BulkValidationException(errors);
        if (!currentUser.IsAdmin || string.IsNullOrWhiteSpace(currentUser.UserId))
            throw new UnauthorizedAccessException("Global administrator access is required.");

        var previous = await repository.GetPersistedAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        BulkImportSettings saved;
        try
        {
            saved = await new BulkAuditWriter(repository).SaveSettingsChangedAsync(
                new BulkImportSettings { MaxFilesPerRequest = command.Request.MaxFilesPerRequest, UpdatedAt = now, UpdatedByActorId = currentUser.UserId },
                command.Request.ExpectedConcurrencyVersion,
                previous?.MaxFilesPerRequest ?? BulkImportSettings.DefaultMaxFilesPerRequest,
                previous?.ConcurrencyVersion ?? 0,
                currentUser.UserId,
                command.CorrelationId,
                now,
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "stale_version")
        {
            throw BulkApiException.Conflict("stale_version", "The bulk scoring settings have changed.");
        }

        return new(saved.MaxFilesPerRequest, saved.ConcurrencyVersion, saved.UpdatedAt, saved.UpdatedByActorId);
    }
}

public sealed class BulkValidationException(IReadOnlyDictionary<string, string[]> errors) : InvalidOperationException("Bulk scoring validation failed.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class PreviewBulkScoringRequestHandler(
    BulkScoringAccess access,
    SourceLinkClassifier classifier,
    DiscoveryStepService steps,
    IBulkScoringRepository repository,
    TimeProvider clock) : IRequestHandler<PreviewBulkScoringRequestCommand, BulkPreviewResponseDto>
{
    public async Task<BulkPreviewResponseDto> Handle(PreviewBulkScoringRequestCommand command, CancellationToken ct)
    {
        await access.RequireJobAsync(command.JobId, mutate: true, ct);
        if (!classifier.TryClassify(command.FolderUrl, out var link)) throw BulkApiException.UnsupportedSource();

        var context = access.InteractiveContext();
        var folder = await access.ResolveAsync(link!.Uri.AbsoluteUri, context, ct);
        if (folder.Kind != link!.Kind) throw BulkApiException.UnsupportedSource();

        var limits = await access.SnapshotLimitsAsync(ct);
        var options = access.Options;
        var draft = new BulkScoringRequest
        {
            JobId = command.JobId,
            OwnerActorId = access.Actor,
            SourceKind = folder.Kind,
            SourceTenantId = folder.TenantId,
            SourceAccountBindingHash = options.Mode == SharePointMode.Mock ? "mock-source-account" : string.Empty,
            SourceDriveId = folder.DriveId,
            SourceFolderItemId = folder.ItemId,
            SourceFolderName = folder.Name,
            SourceFolderPath = folder.Path,
            SourceWebUrl = folder.WebUrl,
            RasterDpi = limits.RasterDpi,
            MaxFilesPerRequest = limits.MaxFilesPerRequest,
            MaxBytesPerFile = limits.MaxBytesPerFile,
            MaxTotalBytes = limits.MaxTotalBytes,
            DiscoveryStepMaxFiles = limits.DiscoveryStepFiles,
            DiscoveryStepMaxEntries = limits.DiscoveryStepMaxEntries,
            DiscoveryStepMaxSeconds = limits.DiscoveryStepMaxSeconds,
            CorrelationId = Guid.TryParse(command.CorrelationId, out _)
                ? command.CorrelationId!
                : Guid.NewGuid().ToString(),
        };

        var outcome = await RunStepAsync(draft, folder, null, context, limits, 0, ct);
        if (outcome.Status == BulkDiscoveryStatus.Complete && outcome.NewItems.Count == 0)
            throw BulkApiException.Invalid("No supported files were found in this folder.");

        var expires = clock.GetUtcNow().UtcDateTime + options.ConnectionIdleTtl;
        var saved = await repository.CreateDraftAsync(draft, outcome.ToStepResult(expires), ct);
        var totals = await repository.GetInclusionTotalsAsync(saved.Id, ct);
        var page = await repository.GetItemsAsync(saved.Id, new BulkItemQuery(1, 100), ct);
        return new BulkPreviewResponseDto(
            saved.Id,
            new BulkFolderDto(folder.Name, folder.Path, folder.WebUrl),
            page.Items.Select(i => new BulkPreviewFileDto(i.Id, i.FileName, i.SourcePath, i.SizeBytes, i.MimeType, i.ExcludedWhenRasterized)).ToArray(),
            access.Progress(saved, totals),
            BulkScoringAccess.LimitsDto(limits),
            saved.ExpiresAt!.Value);
    }

    public sealed class PreviewSourceConnectionHandler(
        BulkScoringAccess access,
        DiscoveryStepService steps,
        IBulkSourceConnectionRepository connections,
        IBulkScoringRepository repository,
        TimeProvider clock) : IRequestHandler<PreviewSourceConnectionCommand, SourceConnectionPreviewResponse>
    {
        public async Task<SourceConnectionPreviewResponse> Handle(
            PreviewSourceConnectionCommand command,
            CancellationToken ct)
        {
            var connection = await connections.GetOwnedAsync(command.ConnectionId, access.Actor, ct)
                ?? throw BulkApiException.NotFound("The source connection was not found.");
            await access.RequireJobAsync(connection.JobId, mutate: true, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            if (connection.Status != BulkSourceConnectionStatus.Verified
                || connection.IsExpired(now)
                || connection.SourceKind is null
                || connection.SourceTenantId is null
                || connection.SourceAccountBindingHash is null
                || connection.SourceDriveId is null
                || connection.SourceFolderItemId is null
                || connection.SourceFolderName is null
                || connection.SourceFolderPath is null
                || connection.SourceWebUrl is null)
                throw new BulkApiException(
                    410,
                    "source_connection_invalid_or_expired",
                    "The source connection is invalid or has expired.");

            var folder = new SourceFolder(
                connection.SourceKind.Value,
                connection.SourceDriveId,
                connection.SourceFolderItemId,
                connection.SourceFolderName,
                connection.SourceFolderPath,
                connection.SourceWebUrl,
                connection.SourceTenantId);
            var limits = await access.SnapshotLimitsAsync(ct);
            var draft = new BulkScoringRequest
            {
                JobId = connection.JobId,
                OwnerActorId = access.Actor,
                SourceKind = folder.Kind,
                SourceTenantId = connection.SourceTenantId,
                SourceAccountBindingHash = connection.SourceAccountBindingHash,
                SourceDriveId = folder.DriveId,
                SourceFolderItemId = folder.ItemId,
                SourceFolderName = folder.Name,
                SourceFolderPath = folder.Path,
                SourceWebUrl = folder.WebUrl,
                ConnectionId = connection.Id,
                RasterDpi = limits.RasterDpi,
                MaxFilesPerRequest = limits.MaxFilesPerRequest,
                MaxBytesPerFile = limits.MaxBytesPerFile,
                MaxTotalBytes = limits.MaxTotalBytes,
                DiscoveryStepMaxFiles = limits.DiscoveryStepFiles,
                DiscoveryStepMaxEntries = limits.DiscoveryStepMaxEntries,
                DiscoveryStepMaxSeconds = limits.DiscoveryStepMaxSeconds,
                CorrelationId = Guid.TryParse(command.CorrelationId, out _)
                    ? command.CorrelationId!
                    : Guid.NewGuid().ToString(),
            };
            var context = access.InteractiveContext(connection.Id);
            DiscoveryStepOutcome outcome;
            try
            {
                outcome = await steps.RunStepAsync(new DiscoveryStepInput(
                    draft.Id,
                    folder,
                    null,
                    context,
                    limits.MaxBytesPerFile,
                    limits.MaxFilesPerRequest,
                    limits.MaxTotalBytes,
                    0,
                    (ids, token) => repository.FindExistingSourceItemIdsAsync(draft.Id, ids, token),
                    limits.DiscoveryStepFiles,
                    limits.DiscoveryStepMaxEntries,
                    TimeSpan.FromSeconds(limits.DiscoveryStepMaxSeconds)), ct);
            }
            catch (SourceAccessDeniedException) { throw BulkApiException.AccessDenied(); }
            catch (SourceNotFoundException) { throw BulkApiException.Invalid("The folder could not be found."); }
            catch (SourceTransientException)
            {
                throw new BulkApiException(
                    502,
                    "source_unavailable",
                    "The folder could not be read right now. Try again shortly.");
            }

            if (outcome.Status == BulkDiscoveryStatus.Complete && outcome.NewItems.Count == 0)
                throw BulkApiException.Invalid("No supported files were found in this folder.");

            var expires = now + access.Options.ConnectionIdleTtl;
            var saved = await repository.CreateDraftAsync(draft, outcome.ToStepResult(expires), ct);
            var totals = await repository.GetInclusionTotalsAsync(saved.Id, ct);
            var page = await repository.GetItemsAsync(saved.Id, new BulkItemQuery(1, 100), ct);
            var progress = access.Progress(saved, totals);
            return new SourceConnectionPreviewResponse(
                saved.Id,
                connection.Id,
                saved.SourceKind.ToWire(),
                connection.SourceAccountDisplay ?? string.Empty,
                saved.SourceFolderName,
                BulkAuthorizationStatus.Active.ToWire(),
                saved.DiscoveryStatus.ToWire(),
                saved.DraftReviewVersion,
                saved.ConcurrencyVersion,
                new BulkCountsDto(
                    saved.DiscoveredCount,
                    saved.ExcludedCount,
                    saved.InaccessibleCount,
                    saved.WaitingCount,
                    saved.ImportingCount,
                    saved.ConvertingCount,
                    saved.ScoringCount,
                    saved.CompletedCount,
                    saved.FailedCount,
                    saved.SkippedCount,
                    saved.CancelledCount),
                saved.GetExcludedByReason().ToDictionary(
                    pair => pair.Key.ToWire(),
                    pair => pair.Value,
                    StringComparer.Ordinal),
                saved.EntriesScanned,
                saved.TotalEligibleBytes,
                new SourceConnectionLimitsDto(
                    saved.MaxFilesPerRequest,
                    saved.MaxBytesPerFile,
                    saved.MaxTotalBytes,
                    saved.DiscoveryStepMaxFiles),
                $"/api/bulk-scoring-requests/{Uri.EscapeDataString(saved.Id)}/draft-items",
                new BulkFolderDto(folder.Name, folder.Path, folder.WebUrl),
                page.Items.Select(item => new BulkPreviewFileDto(
                    item.Id,
                    item.FileName,
                    item.SourcePath,
                    item.SizeBytes,
                    item.MimeType,
                    item.ExcludedWhenRasterized)).ToArray(),
                progress,
                saved.ExpiresAt!.Value);
        }
    }

    private async Task<DiscoveryStepOutcome> RunStepAsync(
        BulkScoringRequest request, SourceFolder folder, string? cursor, SourceCallContext context,
        BulkRequestLimits limits, int nextOrdinal, CancellationToken ct)
    {
        try
        {
            return await steps.RunStepAsync(new DiscoveryStepInput(
                request.Id, folder, cursor, context, limits.MaxBytesPerFile,
                Math.Max(0, limits.MaxFilesPerRequest - request.DiscoveredCount),
                Math.Max(0, limits.MaxTotalBytes - request.TotalEligibleBytes),
                nextOrdinal,
                (ids, token) => repository.FindExistingSourceItemIdsAsync(request.Id, ids, token),
                limits.DiscoveryStepFiles,
                limits.DiscoveryStepMaxEntries,
                TimeSpan.FromSeconds(limits.DiscoveryStepMaxSeconds)), ct);
        }
        catch (SourceAccessDeniedException) { throw BulkApiException.AccessDenied(); }
        catch (SourceNotFoundException) { throw BulkApiException.Invalid("The folder could not be found."); }
        catch (SourceTransientException) { throw new BulkApiException(502, "source_unavailable", "The folder could not be read right now. Try again shortly."); }
        catch (AuthorizationRequiredException) { throw BulkApiException.AuthorizationRequired(); }
    }
}

public sealed class DiscoverMoreBulkScoringRequestHandler(
    BulkScoringAccess access,
    DiscoveryStepService steps,
    IBulkScoringRepository repository,
    TimeProvider clock) : IRequestHandler<DiscoverMoreBulkScoringRequestCommand, BulkDiscoveryStepDto>
{
    public async Task<BulkDiscoveryStepDto> Handle(DiscoverMoreBulkScoringRequestCommand command, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, command.RequestId, mutate: true, ct);
        if (Guid.TryParse(command.CorrelationId, out _))
            request.CorrelationId = command.CorrelationId!;
        var now = clock.GetUtcNow().UtcDateTime;
        if (request.Status != BulkRequestStatus.Draft) throw BulkApiException.Conflict("invalid_state", "Only a preview can be extended.");
        if (request.IsExpired(now)) throw BulkApiException.DraftExpired();
        if (request.DiscoveryStatus != BulkDiscoveryStatus.MoreAvailable)
            throw BulkApiException.Conflict("discovery_finished", "Discovery has already finished.");

        var context = access.InteractiveContext();
        var folder = BulkScoringAccess.FolderOf(request);
        var limits = BulkScoringAccess.FrozenLimits(request);

        DiscoveryStepOutcome outcome;
        try
        {
            outcome = await steps.RunStepAsync(new DiscoveryStepInput(
                request.Id, folder, request.DiscoveryCursorJson, context, limits.MaxBytesPerFile,
                Math.Max(0, limits.MaxFilesPerRequest - request.DiscoveredCount),
                Math.Max(0, limits.MaxTotalBytes - request.TotalEligibleBytes),
                request.DiscoveredCount,
                (ids, token) => repository.FindExistingSourceItemIdsAsync(request.Id, ids, token),
                limits.DiscoveryStepFiles,
                limits.DiscoveryStepMaxEntries,
                TimeSpan.FromSeconds(limits.DiscoveryStepMaxSeconds)), ct);
        }
        catch (SourceAccessDeniedException) { throw BulkApiException.AccessDenied(); }
        catch (SourceNotFoundException) { throw BulkApiException.Invalid("The folder could not be found."); }
        catch (SourceTransientException) { throw new BulkApiException(502, "source_unavailable", "The folder could not be read right now. Try again shortly."); }
        catch (AuthorizationRequiredException) { throw BulkApiException.AuthorizationRequired(); }

        BulkScoringRequest saved;
        try
        {
            saved = await repository.ApplyDiscoveryStepAsync(
                request.Id, request.ConcurrencyVersion, outcome.ToStepResult(now + access.Options.ConnectionIdleTtl), ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "stale_version")
        {
            throw BulkApiException.Conflict("version_conflict", "The preview changed. Reload and try again.");
        }

        var totals = await repository.GetInclusionTotalsAsync(saved.Id, ct);
        return new BulkDiscoveryStepDto(saved.Id, access.Progress(saved, totals), saved.ExpiresAt!.Value);
    }
}

public sealed class ConfirmBulkScoringRequestHandler(
    BulkScoringAccess access,
    IBulkScoringRepository repository,
    IProcessingEventRepository events,
    IJobRepository jobs,
    TimeProvider clock,
    IRequestAuthorizationStore? authorizationStore = null) : IRequestHandler<ConfirmBulkScoringRequestCommand, BulkConfirmResult>
{
    public async Task<BulkConfirmResult> Handle(ConfirmBulkScoringRequestCommand command, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, command.RequestId, mutate: true, ct);
        var job = await access.RequireFreshConfirmationAccessAsync(request, ct);

        // Confirming twice returns the accepted request unchanged.
        if (request.Status != BulkRequestStatus.Draft)
            return new(await access.DetailAsync(request, job, null, ct), false);

        var now = clock.GetUtcNow().UtcDateTime;
        if (request.IsExpired(now)) throw BulkApiException.DraftExpired();
        if (request.DiscoveredCount == 0)
            throw BulkApiException.Invalid("Include at least one file before starting. Choose Discover more to keep scanning.");

        // Resolve again through the verified connection and require the same immutable folder identity.
        await access.RequireFreshExactFolderAccessAsync(request, ct);

        if (authorizationStore is null) throw BulkApiException.Unavailable();
        var acceptedAudit = new ProcessingEvent
        {
            Actor = access.Actor,
            EventType = "bulk-scoring-request.accepted",
            EntityType = nameof(BulkScoringRequest),
            EntityId = request.Id,
            CorrelationId = command.CorrelationId,
            Timestamp = now,
            PayloadJson = JsonSerializer.Serialize(new
            {
                request.JobId,
                sourceKind = request.SourceKind.ToWire(),
                items = request.DiscoveredCount,
                command.Request.ConvertToRasterPdf,
                command.Request.AllowDuplicates,
            }),
        };
        var authorizationAudit = new ProcessingEvent
        {
            Actor = access.Actor,
            EventType = "bulk-scoring-authorization.created",
            EntityType = "BulkRequestAuthorization",
            EntityId = request.Id,
            CorrelationId = command.CorrelationId,
            Timestamp = now,
            PayloadJson = JsonSerializer.Serialize(new
            {
                request.JobId,
                reasonCode = (string?)null,
                itemsFailed = 0,
            }),
        };

        BulkScoringRequest active;
        try
        {
            active = await repository.ConfirmAtomicallyAsync(
                request.Id,
                request.ConcurrencyVersion,
                access.Actor,
                command.Request.ConvertToRasterPdf,
                command.Request.AllowDuplicates,
                job.CurrentConfigVersionId,
                now,
                token => authorizationStore.TransferToRequestAsync(
                    request.Id,
                    request.ConnectionId ?? string.Empty,
                    access.Actor,
                    request.JobId,
                    token),
                authorizationStore.RollbackTransferAsync,
                [acceptedAudit, authorizationAudit],
                events.AddAsync,
                ct);
        }
        catch (AuthorizationRequiredException) { throw BulkApiException.AuthorizationRequired(); }
        catch (InvalidOperationException ex) when (ex.Message == "stale_version")
        {
            throw BulkApiException.Conflict("version_conflict", "The preview changed. Reload and try again.");
        }
        catch (BulkConfirmationPersistenceException ex) when (ex.InnerException?.Message == "stale_version")
        {
            throw BulkApiException.Conflict("version_conflict", "The preview changed. Reload and try again.");
        }
        catch (BulkConfirmationPersistenceException ex)
        {
            throw BulkApiException.Invalid(ex.Message);
        }

        return new(await access.DetailAsync(active, job, null, ct), true);
    }
}

public sealed class AbandonBulkScoringRequestDraftHandler(
    BulkScoringAccess access,
    IBulkScoringRepository repository,
    TimeProvider clock)
    : IRequestHandler<AbandonBulkScoringRequestDraftCommand>
{
    public async Task Handle(AbandonBulkScoringRequestDraftCommand command, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, command.RequestId, mutate: true, ct);
        if (request.Status != BulkRequestStatus.Draft)
            throw BulkApiException.Conflict("invalid_state", "Only a preview can be abandoned.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await repository.DeleteDraftWithAuditAsync(
                request.Id,
                access.Actor,
                BulkAuditWriter.User(
                    BulkAuditEventTypes.PreviewDraftAbandoned,
                    access.Actor,
                    command.CorrelationId ?? request.CorrelationId,
                    now,
                    "bulk_request",
                    request.Id,
                    new { state = "abandoned", configurationVersion = request.ConcurrencyVersion }),
                ct))
            throw BulkApiException.NotFound();
    }
}

public sealed class GetBulkScoringRequestHandler(BulkScoringAccess access, IBulkScoringRepository repository, IJobRepository jobs)
    : IRequestHandler<GetBulkScoringRequestQuery, BulkRequestDetailDto>
{
    public async Task<BulkRequestDetailDto> Handle(GetBulkScoringRequestQuery query, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, query.RequestId, mutate: false, ct);
        var job = await jobs.GetByIdWithoutApplicationsAsync(request.JobId, ct)
            ?? await jobs.GetByIdAsync(request.JobId, ct);
        var totals = request.Status == BulkRequestStatus.Draft ? await repository.GetInclusionTotalsAsync(request.Id, ct) : null;
        return await access.DetailAsync(request, job, totals, ct);
    }
}

public sealed class GetBulkScoringItemsHandler(BulkScoringAccess access, IBulkScoringRepository repository)
    : IRequestHandler<GetBulkScoringItemsQuery, BulkItemPageDto>
{
    public async Task<BulkItemPageDto> Handle(GetBulkScoringItemsQuery query, CancellationToken ct)
    {
        var request = await BulkRequestLoader.LoadAsync(access, repository, query.RequestId, mutate: false, ct);
        BulkItemStatus? status = null;
        if (query.Status is not null)
        {
            if (!WireEnum.TryParse<BulkItemStatus>(query.Status, out var parsed)) throw BulkApiException.Invalid("Unknown status filter.");
            status = parsed;
        }

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var page = await repository.GetItemsAsync(request.Id, new BulkItemQuery(Math.Max(1, query.Page), pageSize, status, query.NameContains), ct);
        return new(
            page.Items.Select(i => new BulkItemDto(
                i.Id, i.FileName, i.SourcePath, i.SizeBytes, i.Status.ToWire(), i.ReasonCode?.ToWire(), i.ReasonMessage, i.Attempt,
                i.ApplicationId, i.ScoredVersion?.ToWire(), i.HasRetainedOriginal, i.ExcludedWhenRasterized, i.MimeType,
                i.PageCount, i.ConvertedSizeBytes, i.ScoringConfigVersionId, i.UpdatedAt)).ToArray(),
            page.Page, page.PageSize, page.Total);
    }
}

// Loads a request the caller may see: drafts are visible to their owner only; active requests to anyone who can read the job.
internal static class BulkRequestLoader
{
    public static async Task<BulkScoringRequest> LoadAsync(
        BulkScoringAccess access, IBulkScoringRepository repository, string requestId, bool mutate, CancellationToken ct)
    {
        access.EnsureEnabled();
        var request = await repository.GetAsync(requestId, ct) ?? throw BulkApiException.NotFound();
        await access.RequireJobAsync(request.JobId, mutate, ct);
        // A preview belongs to the recruiter who started it; an accepted request is visible to anyone who can read the job.
        if (request.Status == BulkRequestStatus.Draft && request.OwnerActorId != access.Actor)
            throw BulkApiException.NotFound();
        return request;
    }
}
