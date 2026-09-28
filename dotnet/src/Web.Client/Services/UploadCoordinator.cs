using System.Net;
using Microsoft.AspNetCore.Components.Forms;

namespace TalentMatch.Web.Client.Services;

public sealed class UploadCapacityScheduler
{
    private readonly int _maxCount;
    private readonly long _maxRawBytes;
    private readonly Dictionary<string, long> _active = [];
    private readonly Queue<string> _waiting = [];
    private readonly Dictionary<string, long> _waitingSizes = [];

    public UploadCapacityScheduler(int maxCount, long maxRawBytes)
    {
        if (maxCount <= 0 || maxRawBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        _maxCount = maxCount;
        _maxRawBytes = maxRawBytes;
    }

    public int ActiveCount => _active.Count;
    public long ActiveRawBytes => _active.Values.Sum();
    public bool IsThrottled(string id) => _waitingSizes.ContainsKey(id);

    public bool TryReserve(string id, long rawBytes)
    {
        if (_active.ContainsKey(id))
            return true;
        if (!_waitingSizes.ContainsKey(id))
        {
            _waiting.Enqueue(id);
            _waitingSizes[id] = rawBytes;
        }
        if (_waiting.Peek() != id
            || _active.Count >= _maxCount
            || ActiveRawBytes + rawBytes > _maxRawBytes)
            return false;
        _waiting.Dequeue();
        _waitingSizes.Remove(id);
        _active[id] = rawBytes;
        return true;
    }

    public void Release(string id) => _active.Remove(id);
}

public sealed class UploadCoordinator(ApiClient api) : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly List<UploadSessionDetailDto> _sessions = [];
    private readonly Dictionary<string, byte[]> _stagedContent = [];
    private readonly object _sync = new();
    public event Action? Changed;
    public event Action<string?>? DetailsRequested;
    public IReadOnlyList<UploadSessionDetailDto> Sessions
    {
        get
        {
            lock (_sync)
                return _sessions.ToArray();
        }
    }

    public void RequestDetails(string? sessionId = null) => DetailsRequested?.Invoke(sessionId);

    public async Task<UploadSessionDetailDto> StartAsync(
        string jobId,
        bool allowDuplicates,
        IReadOnlyList<IBrowserFile> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("Select at least one eligible file.");
        var occurrences = files.Select((file, ordinal) => new LocalOccurrence(
            Guid.NewGuid(),
            ordinal,
            file,
            NormalizeMime(file.Name, file.ContentType))).ToArray();
        var request = new CreateUploadSessionRequest(
            allowDuplicates,
            occurrences.Select(x => new CreateUploadItemRequest(
                x.Key, x.Ordinal, x.File.Name, x.MimeType, x.File.Size)).ToArray());
        var session = await api.CreateUploadSessionAsync(
            jobId, request, Guid.NewGuid().ToString(), cancellationToken);
        lock (_sync)
            _sessions.Insert(0, session);
        Changed?.Invoke();
        try
        {
            await StageAsync(session, occurrences, cancellationToken);
        }
        catch (Exception ex)
        {
            await InterruptSessionAsync(
                session,
                "local_file_unavailable",
                $"The selected browser file could not be staged: {ex.Message}",
                cancellationToken);
            RemoveStagedSession(session.Id);
            throw new InvalidOperationException(
                "The selected files could not be prepared for background upload. Select them again and retry.",
                ex);
        }
        _ = ProcessAsync(session, CancellationToken.None);
        _ = MonitorAsync(session.Id, CancellationToken.None);
        return session;
    }

    private async Task StageAsync(
        UploadSessionDetailDto session,
        IReadOnlyList<LocalOccurrence> occurrences,
        CancellationToken cancellationToken)
    {
        foreach (var occurrence in occurrences)
        {
            var item = session.Items.Single(candidate =>
                candidate.OccurrenceKey == occurrence.Key);
            if (item.Status is "succeeded" or "skipped_duplicate" or "failed" or "interrupted")
                continue;
            await using var source = occurrence.File.OpenReadStream(
                session.Limits.MaxIndividualFileBytes,
                cancellationToken);
            using var destination = new MemoryStream(
                checked((int)occurrence.File.Size));
            await source.CopyToAsync(destination, cancellationToken);
            if (destination.Length != occurrence.File.Size)
                throw new IOException(
                    $"The staged byte length for '{occurrence.File.Name}' did not match the selected file.");
            lock (_sync)
                _stagedContent[StagedKey(session.Id, item.Id)] = destination.ToArray();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var summaries = await api.ListUploadSessionsAsync(cancellationToken: cancellationToken);
        foreach (var summary in summaries)
        {
            var detail = await api.GetUploadSessionAsync(summary.Id, cancellationToken);
            if (detail is not null)
                Replace(detail);
        }
    }

    private async Task ProcessAsync(
        UploadSessionDetailDto session,
        CancellationToken cancellationToken)
    {
        var scheduler = new UploadCapacityScheduler(
            session.Limits.FileConcurrency, session.Limits.MaxInFlightBytes);
        var pending = session.Items
            .Where(x => x.Status is "waiting" or "throttled")
            .OrderBy(x => x.Ordinal)
            .Select(item => new WorkItem(item))
            .ToList();
        var running = new List<Task>();

        while (pending.Count > 0 || running.Count > 0)
        {
            while (pending.Count > 0
                && scheduler.TryReserve(pending[0].Item.Id, pending[0].Item.RawSizeBytes))
            {
                var work = pending[0];
                pending.RemoveAt(0);
                running.Add(RunItemAsync(session, work, scheduler, cancellationToken));
            }

            if (pending.Count > 0)
                await BestEffortStatusAsync(session.Id, pending[0].Item, "throttled", cancellationToken);
            if (running.Count == 0)
                break;
            var finished = await Task.WhenAny(running);
            running.Remove(finished);
            try
            {
                await finished;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-item failures are reconciled by RunItemAsync; never abandon later items.
            }
        }
        await TryReloadAsync(session.Id, cancellationToken);
    }

    private async Task MonitorAsync(string sessionId, CancellationToken cancellationToken)
    {
        var nextHeartbeatAt = DateTime.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UploadSessionDetailDto? local;
                lock (_sync)
                    local = _sessions.FirstOrDefault(session => session.Id == sessionId);
                if (local is null || local.Status == "completed")
                    return;
                if (DateTime.UtcNow >= nextHeartbeatAt)
                {
                    try
                    {
                        await api.HeartbeatUploadSessionAsync(
                            sessionId, local.ConcurrencyVersion, cancellationToken);
                        nextHeartbeatAt = DateTime.UtcNow.Add(HeartbeatInterval);
                    }
                    catch (ApiException ex) when (ex.StatusCode == 409)
                    {
                        // A completed session rejects renewal. It is safe to read final detail because
                        // completed sessions are never stale-reconciled.
                    }
                    catch
                    {
                        // Never request reconciliation-prone detail after a failed renewal. A transient
                        // heartbeat outage must not turn a live browser session into interrupted work.
                        await Task.Delay(PollInterval, cancellationToken);
                        continue;
                    }
                }
                var detail = await api.GetUploadSessionAsync(sessionId, cancellationToken);
                if (detail is null)
                    return;
                Replace(detail);
                if (detail.Status == "completed")
                {
                    RemoveStagedSession(sessionId);
                    return;
                }
            }
            catch
            {
                // Polling is best effort; item requests retain their own retry behavior.
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task RunItemAsync(
        UploadSessionDetailDto session,
        WorkItem work,
        UploadCapacityScheduler scheduler,
        CancellationToken cancellationToken)
    {
        var transportAttemptCount = 0;
        try
        {
            UploadItemDto canonical = work.Item;
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                transportAttemptCount = attempt;
                try
                {
                    await using var stream = OpenStagedStream(session.Id, work.Item.Id);
                    canonical = await api.UploadItemContentAsync(
                        session.Id, canonical, stream, cancellationToken);
                    ReplaceItem(session.Id, canonical);
                    return;
                }
                catch (HttpRequestException ex) when (attempt < 4 && IsTransient(ex.StatusCode))
                {
                    canonical = await BestEffortStatusAsync(
                        session.Id, canonical, "retrying", cancellationToken,
                        "transient_failure", "The upload will be retried.",
                        DateTime.UtcNow.AddMilliseconds(100 * attempt),
                        attempt) ?? canonical;
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var detail = await TryReloadAsync(session.Id, cancellationToken);
            var persisted = detail?.Items.FirstOrDefault(item => item.Id == work.Item.Id);
            if (persisted is not null
                && persisted.Status is "succeeded" or "skipped_duplicate" or "failed" or "interrupted")
                return;

            if (ex is HttpRequestException requestError
                && IsTransient(requestError.StatusCode)
                && transportAttemptCount == 4)
            {
                await BestEffortStatusAsync(
                    session.Id,
                    persisted ?? work.Item,
                    "failed",
                    cancellationToken,
                    "retry_exhausted",
                    "Upload failed after four browser transport attempts. Try selecting the file again.",
                    transportAttemptCount: transportAttemptCount);
            }
            else
            {
                await BestEffortStatusAsync(
                    session.Id,
                    persisted ?? work.Item,
                    "interrupted",
                    cancellationToken,
                    "local_staging_failure",
                    "The browser could not read the staged file. Select the file again and retry.");
            }
            await TryReloadAsync(session.Id, cancellationToken);
        }
        finally
        {
            RemoveStagedItem(session.Id, work.Item.Id);
            scheduler.Release(work.Item.Id);
            Changed?.Invoke();
        }
    }

    private async Task<UploadSessionDetailDto?> TryReloadAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await api.GetUploadSessionAsync(sessionId, cancellationToken);
            if (detail is not null)
                Replace(detail);
            return detail;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or System.Text.Json.JsonException)
        {
            // MonitorAsync remains the durable reconciliation fallback.
            return null;
        }
    }

    private async Task<UploadItemDto?> BestEffortStatusAsync(
        string sessionId,
        UploadItemDto item,
        string status,
        CancellationToken cancellationToken,
        string? outcomeCode = null,
        string? outcomeMessage = null,
        DateTime? nextRetryAt = null,
        int? transportAttemptCount = null)
    {
        try
        {
            var updated = await api.UpdateUploadItemStatusAsync(
                sessionId,
                item.Id,
                new(item.OccurrenceKey, status, item.ConcurrencyVersion,
                    outcomeCode, outcomeMessage, nextRetryAt, transportAttemptCount),
                cancellationToken);
            ReplaceItem(sessionId, updated);
            return updated;
        }
        catch
        {
            // Capacity status is informative; canonical state is refreshed by polling.
            return null;
        }
    }

    private void ReplaceItem(string sessionId, UploadItemDto item)
    {
        lock (_sync)
        {
            var index = _sessions.FindIndex(x => x.Id == sessionId);
            if (index < 0)
                return;
            var items = _sessions[index].Items.Select(x => x.Id == item.Id ? item : x).ToArray();
            _sessions[index] = _sessions[index] with { Items = items };
        }
        Changed?.Invoke();
    }

    private void Replace(UploadSessionDetailDto session)
    {
        lock (_sync)
        {
            var index = _sessions.FindIndex(x => x.Id == session.Id);
            if (index >= 0)
                _sessions[index] = session;
            else
                _sessions.Add(session);
        }
        Changed?.Invoke();
    }

    private static bool IsTransient(HttpStatusCode? status) =>
        status is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private Stream OpenStagedStream(string sessionId, string itemId)
    {
        lock (_sync)
        {
            if (!_stagedContent.TryGetValue(StagedKey(sessionId, itemId), out var content))
                throw new IOException("The staged browser file is no longer available.");
            return new MemoryStream(content, writable: false);
        }
    }

    private async Task InterruptSessionAsync(
        UploadSessionDetailDto session,
        string outcomeCode,
        string outcomeMessage,
        CancellationToken cancellationToken)
    {
        foreach (var item in session.Items.Where(candidate =>
                     candidate.Status is "waiting" or "throttled"))
        {
            await BestEffortStatusAsync(
                session.Id,
                item,
                "interrupted",
                cancellationToken,
                outcomeCode,
                outcomeMessage);
        }
    }

    private void RemoveStagedItem(string sessionId, string itemId)
    {
        lock (_sync)
            _stagedContent.Remove(StagedKey(sessionId, itemId));
    }

    private void RemoveStagedSession(string sessionId)
    {
        lock (_sync)
        {
            foreach (var key in _stagedContent.Keys
                         .Where(key => key.StartsWith($"{sessionId}:", StringComparison.Ordinal))
                         .ToArray())
                _stagedContent.Remove(key);
        }
    }

    private static string StagedKey(string sessionId, string itemId)
        => $"{sessionId}:{itemId}";

    private static string NormalizeMime(string fileName, string? supplied) =>
        !string.IsNullOrWhiteSpace(supplied) ? supplied : Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".txt" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };

    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions.Where(x => x.Status == "active"))
        {
            foreach (var item in session.Items.Where(x =>
                         x.Status is "waiting" or "throttled" or "uploading" or "retrying"))
            {
                try
                {
                    await api.UpdateUploadItemStatusAsync(
                        session.Id,
                        item.Id,
                        new(
                            item.OccurrenceKey,
                            "interrupted",
                            item.ConcurrencyVersion,
                            "tab_interrupted",
                            "The originating browser tab is no longer uploading this file."));
                }
                catch
                {
                    // Browser teardown is not reliable; heartbeat reconciliation is authoritative.
                }
            }
        }
        lock (_sync)
            _stagedContent.Clear();
    }

    private sealed record LocalOccurrence(Guid Key, int Ordinal, IBrowserFile File, string MimeType);
    private sealed record WorkItem(UploadItemDto Item);
}
