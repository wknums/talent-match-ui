using System.Data;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class UploadSessionRepository(AppDbContext context) : IUploadSessionRepository
{
    public Task<UploadSession> CreateAsync(
        UploadSession session,
        IReadOnlyCollection<UploadItem> items,
        ProcessingEvent createdEvent,
        int expectedSettingsConcurrencyVersion,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var persistedSettings = await context.UploadSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == UploadSettings.SingletonId, ct);
            var effectiveSettings = persistedSettings ?? UploadSettings.Defaults();
            if ((persistedSettings?.ConcurrencyVersion ?? 0) != expectedSettingsConcurrencyVersion
                || effectiveSettings.FileConcurrency != session.FileConcurrency
                || effectiveSettings.MaxIndividualFileBytes != session.MaxIndividualFileBytes
                || effectiveSettings.MaxInFlightBytes != session.MaxInFlightBytes)
                throw new InvalidOperationException("settings_stale");

            session.Items = items.OrderBy(x => x.Ordinal).ToList();
            session.ApplyAggregates(session.Items, session.CreatedAt);
            session.ValidateInvariants();
            await context.UploadSessions.AddAsync(session, ct);
            await context.ProcessingEvents.AddAsync(createdEvent, ct);
            await context.SaveChangesAsync(ct);
            return session;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<UploadSession?> GetOwnedAsync(
        string sessionId,
        string ownerActorId,
        bool includeItems = true,
        CancellationToken cancellationToken = default)
    {
        IQueryable<UploadSession> query = context.UploadSessions.AsNoTracking();
        if (includeItems)
            query = query.Include(x => x.Items.OrderBy(i => i.Ordinal));
        return query.SingleOrDefaultAsync(
            x => x.Id == sessionId && x.OwnerActorId == ownerActorId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<UploadSession>> ListOwnedAsync(
        string ownerActorId,
        string? jobId = null,
        bool includeTerminal = true,
        CancellationToken cancellationToken = default)
    {
        var query = context.UploadSessions.AsNoTracking()
            .Where(x => x.OwnerActorId == ownerActorId);
        if (!string.IsNullOrWhiteSpace(jobId))
            query = query.Where(x => x.JobId == jobId);
        if (!includeTerminal)
            query = query.Where(x => x.Status != UploadSessionStatuses.Completed);
        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
    }

    public Task<UploadItem?> GetItemAsync(
        string sessionId,
        string itemId,
        string ownerActorId,
        CancellationToken cancellationToken = default) =>
        context.UploadItems.AsNoTracking()
            .Where(x => x.Id == itemId && x.SessionId == sessionId)
            .Where(x => x.Session!.OwnerActorId == ownerActorId)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<UploadItem> UpdateItemAsync(
        UploadItem item,
        int expectedConcurrencyVersion,
        IReadOnlyCollection<ProcessingEvent> events,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var tracked = await context.UploadItems
                .SingleOrDefaultAsync(x => x.Id == item.Id && x.SessionId == item.SessionId, ct)
                ?? throw new KeyNotFoundException("Upload item was not found.");
            if (tracked.Status.IsTerminal())
                return tracked;
            if (tracked.ConcurrencyVersion != expectedConcurrencyVersion)
                throw new InvalidOperationException("stale_version");

            var priorStatus = tracked.Status;
            context.Entry(tracked).CurrentValues.SetValues(item);
            foreach (var evt in events)
                await context.ProcessingEvents.AddAsync(evt, ct);
            await context.SaveChangesAsync(ct);
            await ApplySessionTransitionAsync(
                tracked.SessionId,
                priorStatus,
                tracked.Status,
                tracked.UpdatedAt,
                ct);
            return tracked;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<UploadItem> CompleteItemAsync(
        string sessionId,
        string itemId,
        string ownerActorId,
        string occurrenceKey,
        string fingerprint,
        string fileName,
        string mimeType,
        long rawSizeBytes,
        string contentBase64,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var item = await context.UploadItems
                .Include(x => x.Session)
                .SingleOrDefaultAsync(
                    x => x.Id == itemId
                        && x.SessionId == sessionId
                        && x.Session!.OwnerActorId == ownerActorId,
                    ct)
                ?? throw new KeyNotFoundException("Upload item was not found.");
            if (!string.Equals(item.OccurrenceKey, occurrenceKey, StringComparison.Ordinal))
                throw new InvalidOperationException("occurrence_key_mismatch");
            if (item.Status.IsTerminal())
                return item;
            if (item.Status != UploadItemStatus.Uploading)
                throw new InvalidOperationException("attempt_not_active");

            item.ContentFingerprint = fingerprint;
            if (!item.Session!.AllowDuplicates)
            {
                var earlierClaim = await context.UploadItems.AnyAsync(
                    x => x.SessionId == sessionId
                        && x.Id != item.Id
                        && x.ContentFingerprint == fingerprint
                        && (x.Status == UploadItemStatus.Succeeded || x.Status == UploadItemStatus.Uploading),
                    ct);
                var existingForJob = await context.ApplicationDocuments
                    .Join(
                        context.Applications,
                        document => document.ApplicationId,
                        application => application.Id,
                        (document, application) => new { document.Fingerprint, application.JobId })
                    .AnyAsync(x => x.JobId == item.Session.JobId && x.Fingerprint == fingerprint, ct);
                if (earlierClaim || existingForJob)
                {
                    var priorStatus = item.Status;
                    item.TransitionTo(
                        UploadItemStatus.SkippedDuplicate,
                        DateTime.UtcNow,
                        existingForJob ? "duplicate_existing" : "duplicate_selection",
                        existingForJob
                            ? "This document already belongs to the job."
                            : "The same document was already selected for this upload session.");
                    var completesSession = item.Session.TerminalItemCount + 1
                        == item.Session.TotalItemCount;
                    await AddCompletionEventsAsync(item, completesSession, ct);
                    await context.SaveChangesAsync(ct);
                    await ApplySessionTransitionAsync(
                        item.SessionId,
                        priorStatus,
                        item.Status,
                        item.UpdatedAt,
                        ct);
                    return item;
                }
            }

            var application = new TalentMatch.Domain.Entities.Application
            {
                JobId = item.Session.JobId,
                CandidateRef = $"candidate-{Guid.NewGuid().ToString("N")[..8]}",
                CandidateName = DeriveCandidateName(fileName),
                Status = "Queued",
            };
            var document = new ApplicationDocument
            {
                ApplicationId = application.Id,
                FileName = fileName,
                FileType = mimeType,
                FileSize = rawSizeBytes,
                Fingerprint = fingerprint,
                UploadTimestamp = DateTime.UtcNow,
            };
            await context.Applications.AddAsync(application, ct);
            await context.ApplicationDocuments.AddAsync(document, ct);
            await context.DocumentBlobs.AddAsync(new DocumentBlob
            {
                DocumentId = document.Id,
                Content = contentBase64,
            }, ct);
            item.LinkApplication(application.Id);
            var previousStatus = item.Status;
            item.TransitionTo(UploadItemStatus.Succeeded, DateTime.UtcNow);
            var sessionCompletes = item.Session.TerminalItemCount + 1
                == item.Session.TotalItemCount;
            await AddCompletionEventsAsync(item, sessionCompletes, ct);
            await context.SaveChangesAsync(ct);
            await ApplySessionTransitionAsync(
                item.SessionId,
                previousStatus,
                item.Status,
                item.UpdatedAt,
                ct);
            return item;
        }, cancellationToken, IsolationLevel.Serializable);

    private async Task AddCompletionEventsAsync(
        UploadItem item,
        bool sessionCompletes,
        CancellationToken cancellationToken)
    {
        var session = item.Session!;
        await context.ProcessingEvents.AddAsync(new ProcessingEvent
        {
            Actor = session.OwnerActorId,
            EventType = "upload-item.completed",
            EntityType = nameof(UploadItem),
            EntityId = item.Id,
            CorrelationId = session.CorrelationId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = item.Status.ToWireValue(),
                item.ApplicationId,
                item.RawSizeBytes,
                item.AttemptCount,
                item.OutcomeCode,
            }),
        }, cancellationToken);
        if (sessionCompletes)
        {
            await context.ProcessingEvents.AddAsync(new ProcessingEvent
            {
                Actor = session.OwnerActorId,
                EventType = "upload-session.completed",
                EntityType = nameof(UploadSession),
                EntityId = session.Id,
                CorrelationId = session.CorrelationId,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    session.SucceededCount,
                    session.SkippedCount,
                    session.FailedCount,
                    session.InterruptedCount,
                }),
            }, cancellationToken);
        }
    }

    private static string DeriveCandidateName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
        return string.IsNullOrWhiteSpace(name) ? "Unknown Applicant" : name;
    }

    public Task<UploadSession> HeartbeatAsync(
        string sessionId,
        string ownerActorId,
        int expectedConcurrencyVersion,
        DateTime utcNow,
        ProcessingEvent heartbeatEvent,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var updated = await context.UploadSessions
                .Where(session =>
                    session.Id == sessionId
                    && session.OwnerActorId == ownerActorId
                    && session.Status == UploadSessionStatuses.Active)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(session => session.LastHeartbeatAt, utcNow)
                    .SetProperty(session => session.ConcurrencyVersion,
                        session => session.ConcurrencyVersion + 1), ct);
            if (updated == 0)
            {
                var exists = await context.UploadSessions.AsNoTracking().AnyAsync(
                    session => session.Id == sessionId
                        && session.OwnerActorId == ownerActorId, ct);
                if (!exists)
                    throw new KeyNotFoundException("Upload session was not found.");
                throw new InvalidOperationException("stale_version");
            }
            await context.ProcessingEvents.AddAsync(heartbeatEvent, ct);
            await context.SaveChangesAsync(ct);
            return await context.UploadSessions.AsNoTracking().SingleAsync(
                session => session.Id == sessionId
                    && session.OwnerActorId == ownerActorId, ct);
        }, cancellationToken);

    private async Task ApplySessionTransitionAsync(
        string sessionId,
        UploadItemStatus priorStatus,
        UploadItemStatus nextStatus,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        if (priorStatus == nextStatus)
            return;

        var waitingDelta = IsWaiting(nextStatus) - IsWaiting(priorStatus);
        var activeDelta = IsActive(nextStatus) - IsActive(priorStatus);
        var succeededDelta = Is(nextStatus, UploadItemStatus.Succeeded)
            - Is(priorStatus, UploadItemStatus.Succeeded);
        var skippedDelta = Is(nextStatus, UploadItemStatus.SkippedDuplicate)
            - Is(priorStatus, UploadItemStatus.SkippedDuplicate);
        var failedDelta = Is(nextStatus, UploadItemStatus.Failed)
            - Is(priorStatus, UploadItemStatus.Failed);
        var interruptedDelta = Is(nextStatus, UploadItemStatus.Interrupted)
            - Is(priorStatus, UploadItemStatus.Interrupted);
        var terminalDelta = IsTerminal(nextStatus) - IsTerminal(priorStatus);

        var updated = await context.UploadSessions
            .Where(session => session.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.WaitingCount,
                    session => session.WaitingCount + waitingDelta)
                .SetProperty(session => session.ActiveCount,
                    session => session.ActiveCount + activeDelta)
                .SetProperty(session => session.SucceededCount,
                    session => session.SucceededCount + succeededDelta)
                .SetProperty(session => session.SkippedCount,
                    session => session.SkippedCount + skippedDelta)
                .SetProperty(session => session.FailedCount,
                    session => session.FailedCount + failedDelta)
                .SetProperty(session => session.InterruptedCount,
                    session => session.InterruptedCount + interruptedDelta)
                .SetProperty(session => session.TerminalItemCount,
                    session => session.TerminalItemCount + terminalDelta)
                .SetProperty(session => session.Status,
                    session => session.TerminalItemCount + terminalDelta == session.TotalItemCount
                        ? UploadSessionStatuses.Completed
                        : UploadSessionStatuses.Active)
                .SetProperty(session => session.CompletedAt,
                    session => session.TerminalItemCount + terminalDelta == session.TotalItemCount
                        ? session.CompletedAt ?? utcNow
                        : null)
                .SetProperty(session => session.ConcurrencyVersion,
                    session => session.ConcurrencyVersion + 1), cancellationToken);
        if (updated != 1)
            throw new KeyNotFoundException("Upload session was not found.");
    }

    private static int IsWaiting(UploadItemStatus status)
        => status is UploadItemStatus.Waiting or UploadItemStatus.Throttled ? 1 : 0;

    private static int IsActive(UploadItemStatus status)
        => status is UploadItemStatus.Uploading or UploadItemStatus.Retrying ? 1 : 0;

    private static int IsTerminal(UploadItemStatus status)
        => status.IsTerminal() ? 1 : 0;

    private static int Is(UploadItemStatus status, UploadItemStatus expected)
        => status == expected ? 1 : 0;

    public Task<UploadSession?> ReconcileStaleAsync(
        string sessionId,
        string ownerActorId,
        DateTime staleBefore,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var session = await context.UploadSessions
                .Include(x => x.Items)
                .SingleOrDefaultAsync(x => x.Id == sessionId && x.OwnerActorId == ownerActorId, ct);
            if (session is null || session.Status == UploadSessionStatuses.Completed
                || session.LastHeartbeatAt >= staleBefore)
                return session;

            foreach (var item in session.Items.Where(x => !x.Status.IsTerminal()))
                item.TransitionTo(
                    UploadItemStatus.Interrupted,
                    utcNow,
                    "tab_interrupted",
                    "The originating browser tab is no longer uploading this file.");
            session.ApplyAggregates(session.Items, utcNow);
            session.ConcurrencyVersion++;
            await context.SaveChangesAsync(ct);
            return session;
        }, cancellationToken, IsolationLevel.Serializable);
}
