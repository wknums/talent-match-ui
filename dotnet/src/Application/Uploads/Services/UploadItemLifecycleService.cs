using System.Net;
using System.Text.Json;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Uploads.Services;

public sealed class UploadItemLifecycleService
{
    public static readonly TimeSpan HeartbeatLease = TimeSpan.FromMinutes(2);
    private readonly IUploadSessionRepository? _sessions;
    private readonly ICurrentUserService? _currentUser;
    private readonly IJobRepository? _jobs;
    private readonly IOrganizationRepository? _organizations;
    private readonly IApplicationRepository? _applications;
    private readonly IScoringQueueSignal? _queueSignal;
    private readonly TimeProvider _timeProvider;

    public UploadItemLifecycleService(
        IUploadSessionRepository? sessions = null,
        ICurrentUserService? currentUser = null,
        TimeProvider? timeProvider = null,
        IJobRepository? jobs = null,
        IOrganizationRepository? organizations = null,
        IApplicationRepository? applications = null,
        IScoringQueueSignal? queueSignal = null)
    {
        _sessions = sessions;
        _currentUser = currentUser;
        _jobs = jobs;
        _organizations = organizations;
        _applications = applications;
        _queueSignal = queueSignal;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static bool IsTransient(HttpStatusCode? status) =>
        status is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    public void RecordFailure(UploadItem item, HttpStatusCode? status, DateTime utcNow)
    {
        item.LastHttpStatus = status is null ? null : (int)status.Value;
        if (IsTransient(status) && item.AttemptCount < UploadItem.MaxAttempts)
        {
            item.TransitionTo(
                UploadItemStatus.Retrying,
                utcNow,
                "transient_failure",
                "The upload will be retried.",
                utcNow.AddSeconds(Math.Pow(2, Math.Max(0, item.AttemptCount - 1))));
            return;
        }
        item.TransitionTo(
            UploadItemStatus.Failed,
            utcNow,
            IsTransient(status) ? "retry_exhausted" : "permanent_failure",
            IsTransient(status)
                ? "The upload failed after four attempts."
                : "The upload cannot be retried.");
    }

    public void MarkClientStatus(UploadItem item, UploadItemStatus status, DateTime utcNow)
    {
        if (status is not (UploadItemStatus.Waiting
            or UploadItemStatus.Throttled
            or UploadItemStatus.Retrying
            or UploadItemStatus.Interrupted))
            throw new InvalidOperationException("client_status_not_allowed");
        item.TransitionTo(status, utcNow);
    }

    public async Task<IReadOnlyList<UploadSessionSummaryDto>> ListOwnedAsync(
        string? jobId,
        bool includeTerminal,
        CancellationToken cancellationToken)
    {
        var owner = RequireOwner();
        var sessions = await RequireSessions().ListOwnedAsync(
            owner, jobId, includeTerminal, cancellationToken);
        var authorized = new List<UploadSessionSummaryDto>(sessions.Count);
        foreach (var session in sessions)
        {
            try
            {
                await EnsureJobAuthorizationAsync(session, mutate: false, cancellationToken);
                authorized.Add(session.ToSummaryDto());
            }
            catch (Exception ex) when (ex is KeyNotFoundException or UnauthorizedAccessException)
            {
                // A list contains only sessions whose current job remains readable.
            }
        }
        return authorized;
    }

    public async Task<UploadSessionDetailDto?> GetOwnedAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var owner = RequireOwner();
        var header = await RequireSessions().GetOwnedAsync(
            sessionId, owner, includeItems: false, cancellationToken);
        if (header is null)
            return null;
        await EnsureJobAuthorizationAsync(header, mutate: false, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var session = await RequireSessions().ReconcileStaleAsync(
            sessionId, owner, now - HeartbeatLease, now, cancellationToken);
        if (session is not null)
            await ReplayPendingPublicationsAsync(session, cancellationToken);
        return session?.ToDetailDto();
    }

    public async Task<UploadSessionSummaryDto> HeartbeatAsync(
        string sessionId,
        int expectedVersion,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var owner = RequireOwner();
        var sessionHeader = await RequireSessions().GetOwnedAsync(
            sessionId, owner, includeItems: false, cancellationToken)
            ?? throw new KeyNotFoundException("Upload session was not found.");
        await EnsureJobAuthorizationAsync(sessionHeader, mutate: true, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var session = await RequireSessions().HeartbeatAsync(
            sessionId,
            owner,
            expectedVersion,
            now,
            Event("upload-session.heartbeat", nameof(UploadSession), sessionId, owner,
                correlationId, now, new { expectedVersion }),
            cancellationToken);
        return session.ToSummaryDto();
    }

    public async Task<UploadItemDto> UpdateClientStatusAsync(
        string sessionId,
        string itemId,
        UpdateUploadItemStatusRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var owner = RequireOwner();
        var session = await RequireSessions().GetOwnedAsync(
            sessionId, owner, includeItems: false, cancellationToken)
            ?? throw new KeyNotFoundException("Upload session was not found.");
        await EnsureJobAuthorizationAsync(session, mutate: true, cancellationToken);
        var item = await RequireSessions().GetItemAsync(
            sessionId, itemId, owner, cancellationToken)
            ?? throw new KeyNotFoundException("Upload item was not found.");
        if (!string.Equals(item.OccurrenceKey, request.OccurrenceKey.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("occurrence_key_mismatch");
        if (item.Status.IsTerminal())
            return item.ToDto();
        if (request.TransportAttemptCount is < 1 or > UploadItem.MaxAttempts)
            throw new UploadValidationException(
                new Dictionary<string, string[]>
                {
                    ["transportAttemptCount"] = ["Transport attempt count must be a whole number from 1 through 4."],
                });
        if (request.Status == "failed"
            && (request.OutcomeCode != "retry_exhausted"
                || request.TransportAttemptCount != UploadItem.MaxAttempts
                || string.IsNullOrWhiteSpace(request.OutcomeMessage)
                || request.NextRetryAt is not null))
            throw new UploadValidationException(
                new Dictionary<string, string[]>
                {
                    ["status"] = ["Failed is allowed only after exactly four browser transport attempts."],
                });
        var status = request.Status switch
        {
            "waiting" => UploadItemStatus.Waiting,
            "throttled" => UploadItemStatus.Throttled,
            "retrying" => UploadItemStatus.Retrying,
            "failed" => UploadItemStatus.Failed,
            "interrupted" => UploadItemStatus.Interrupted,
            _ => throw new UploadValidationException(
                new Dictionary<string, string[]> { ["status"] = ["The requested client status is not allowed."] }),
        };
        var prior = item.Status;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (status == UploadItemStatus.Failed)
            item.TransitionTo(status, now, request.OutcomeCode, request.OutcomeMessage);
        else
            MarkClientStatus(item, status, now);
        item.OutcomeCode = request.OutcomeCode;
        item.OutcomeMessage = request.OutcomeMessage;
        item.NextRetryAt = status == UploadItemStatus.Retrying ? request.NextRetryAt : null;
        var updated = await RequireSessions().UpdateItemAsync(
            item,
            request.ExpectedConcurrencyVersion,
            [Event(status.IsTerminal() ? "upload-item.completed" : "upload-item.state-changed",
                nameof(UploadItem), item.Id, owner,
                correlationId, now, new
                {
                    previousStatus = prior.ToWireValue(),
                    newStatus = status.ToWireValue(),
                    attempt = item.AttemptCount,
                    request.TransportAttemptCount,
                    item.OutcomeCode,
                })],
            cancellationToken);
        return updated.ToDto();
    }

    private async Task EnsureJobAuthorizationAsync(
        UploadSession session,
        bool mutate,
        CancellationToken cancellationToken)
    {
        if (_jobs is null)
            return;
        var job = await _jobs.GetByIdAsync(session.JobId, cancellationToken)
            ?? throw new KeyNotFoundException("Job was not found.");
        if (mutate)
            await JobAuthorization.EnsureCanMutateAsync(
                job, _currentUser, _organizations, cancellationToken);
        else
            await JobAuthorization.EnsureCanReadAsync(
                job, _currentUser, _organizations, cancellationToken);
    }

    private async Task ReplayPendingPublicationsAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        if (_applications is null)
            return;
        var pending = new List<string>();
        foreach (var applicationId in session.Items
                     .Where(item => item.Status == UploadItemStatus.Succeeded)
                     .Select(item => item.ApplicationId)
                     .Where(id => id is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            var application = await _applications.GetByIdAsync(applicationId, cancellationToken);
            if (application?.Status == "Uploading")
                pending.Add(applicationId);
        }
        if (pending.Count == 0)
            return;
        await _applications.PublishUploadedAsync(pending, cancellationToken);
        _queueSignal?.Pulse();
    }

    private string RequireOwner() =>
        !string.IsNullOrWhiteSpace(_currentUser?.UserId)
            ? _currentUser.UserId!
            : throw new UnauthorizedAccessException("Authentication is required.");

    private IUploadSessionRepository RequireSessions() =>
        _sessions ?? throw new InvalidOperationException("Upload persistence is not configured.");

    private static ProcessingEvent Event(
        string eventType,
        string entityType,
        string entityId,
        string actor,
        string correlationId,
        DateTime timestamp,
        object details) => new()
        {
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            Actor = actor,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            PayloadJson = JsonSerializer.Serialize(details),
        };
}
