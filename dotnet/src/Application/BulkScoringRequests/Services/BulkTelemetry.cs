using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

// One place for the audit ledger entries, structured logs and metrics of bulk scoring. Entries carry
// identifiers and reason codes only: never file names, file content or token material.
public sealed class BulkTelemetry(IProcessingEventRepository events, ILogger<BulkTelemetry> logger, TimeProvider clock)
{
    public const string SystemActor = "system";
    public const string MeterName = "TalentMatch.BulkScoring";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Items = Meter.CreateCounter<long>("bulk.items", description: "Items that reached a state, by outcome and reason code");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("bulk.item.retries", description: "Items put back to wait after a transient failure");
    private static readonly Counter<long> Throttled = Meter.CreateCounter<long>("bulk.source.throttled", description: "Transient failures that carried a Retry-After from the source");
    private static readonly Counter<long> ConvertedPages = Meter.CreateCounter<long>("bulk.converted.pages", description: "Pages in converted documents");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("bulk.requests.completed", description: "Requests that finished, by outcome");
    private static readonly Histogram<double> ItemDuration = Meter.CreateHistogram<double>("bulk.item.duration", "ms", "Time an item spent in one worker run");
    private static readonly Histogram<double> PhaseDuration = Meter.CreateHistogram<double>("bulk.item.phase.duration", "ms", "Time one phase of an item took: download, source_pdf (the source's own conversion to PDF) or rasterize");
    private static readonly Histogram<long> RasterizeOutputBytes = Meter.CreateHistogram<long>("bulk.rasterize.output.bytes", "By", "Size of the rasterized PDF");
    private static readonly Counter<long> RasterizeStepDowns = Meter.CreateCounter<long>("bulk.rasterize.stepdowns", description: "Items rasterized a second time at a lower resolution because the first output was too large");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static class Phases
    {
        public const string Download = "download";
        public const string SourcePdf = "source_pdf";
        public const string Rasterize = "rasterize";
    }

    // A small, fixed set of file kinds: a safe metric dimension that never exposes a file name.
    public static string KindOf(string? mimeType)
    {
        var mime = (mimeType ?? string.Empty).ToLowerInvariant();
        if (mime == "application/pdf") return "pdf";
        if (mime.Contains("wordprocessingml", StringComparison.Ordinal) || mime == "application/msword") return "word";
        if (mime == "text/markdown") return "markdown";
        if (mime == "text/plain") return "text";
        if (mime.StartsWith("image/", StringComparison.Ordinal)) return "image";
        return "other";
    }

    // One phase of an item (download, or the source's own conversion to PDF). Identifiers and codes only.
    public void Phase(BulkScoringRequest request, BulkScoringItem item, string phase, string kind, string outcome, long durationMs)
    {
        PhaseDuration.Record(durationMs,
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("outcome", outcome));
        logger.LogInformation(
            "bulk item phase completed {Phase} {CorrelationId} {RequestId} {ItemId} {JobId} {Attempt} {Kind} {Outcome} {DurationMs}",
            phase, request.CorrelationId, request.Id, item.Id, request.JobId, item.Attempt, kind, outcome, durationMs);
    }

    // The local rasterize phase, with what it produced. DurationMs covers both passes when the output had to be redone smaller.
    public void Rasterized(BulkScoringRequest request, BulkScoringItem item, string kind, string outcome, long durationMs,
        int pages, long outputBytes, int dpi, bool steppedDown)
    {
        PhaseDuration.Record(durationMs,
            new KeyValuePair<string, object?>("phase", Phases.Rasterize),
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("outcome", outcome));
        if (outcome == "ok")
            RasterizeOutputBytes.Record(outputBytes, new KeyValuePair<string, object?>("kind", kind));
        if (steppedDown)
            RasterizeStepDowns.Add(1, new KeyValuePair<string, object?>("kind", kind));
        logger.LogInformation(
            "bulk item phase completed {Phase} {CorrelationId} {RequestId} {ItemId} {JobId} {Attempt} {Kind} {Outcome} {DurationMs} {Pages} {OutputBytes} {Dpi} {SteppedDown}",
            Phases.Rasterize, request.CorrelationId, request.Id, item.Id, request.JobId, item.Attempt, kind, outcome, durationMs,
            pages, outputBytes, dpi, steppedDown);
    }

    public static string StageOf(BulkScoringItem item) =>
        item.SourceStoredAt is null ? "import" : item.ApplicationCreatedAt is null ? "prepare" : "hand_off";

    public async Task ItemAsync(BulkScoringRequest request, BulkScoringItem item, string outcome, long durationMs, string? actor = null)
    {
        var reason = item.ReasonCode?.ToWire();
        var stage = StageOf(item);
        Items.Add(1, new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("reason", reason ?? "none"));
        ItemDuration.Record(durationMs, new KeyValuePair<string, object?>("outcome", outcome));
        if (item.PageCount is { } pages && outcome == "handed_off" && item.ScoredVersion == BulkScoredVersion.Converted)
            ConvertedPages.Add(pages);

        Log(request, item.Id, stage, item.Attempt, reason, durationMs, $"bulk item {outcome}");
        await WriteAsync(new ProcessingEvent
        {
            Actor = actor ?? SystemActor,
            EventType = $"bulk-scoring-item.{outcome}",
            EntityType = nameof(BulkScoringItem),
            EntityId = item.Id,
            CorrelationId = request.CorrelationId,
            Timestamp = Now,
            PayloadJson = JsonSerializer.Serialize(new { requestId = request.Id, jobId = request.JobId, stage, attempt = item.Attempt, reasonCode = reason }),
        });
    }

    public void ItemRetrying(BulkScoringRequest request, BulkScoringItem item, TimeSpan? retryAfter, long durationMs)
    {
        Retries.Add(1);
        if (retryAfter is not null) Throttled.Add(1);
        Log(request, item.Id, StageOf(item), item.Attempt, null, durationMs, "bulk item will be retried");
    }

    public async Task RequestCompletedAsync(BulkScoringRequest request, string? actor = null)
    {
        var outcome = request.Outcome?.ToWire() ?? "unknown";
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Log(request, null, "finalize", 0, null, 0, $"bulk request completed ({outcome})");
        await WriteAsync(new ProcessingEvent
        {
            Actor = actor ?? SystemActor,
            EventType = "bulk-scoring-request.completed",
            EntityType = nameof(BulkScoringRequest),
            EntityId = request.Id,
            CorrelationId = request.CorrelationId,
            Timestamp = Now,
            PayloadJson = JsonSerializer.Serialize(new
            {
                request.JobId, outcome,
                completed = request.CompletedCount, failed = request.FailedCount, skipped = request.SkippedCount, cancelled = request.CancelledCount,
            }),
        });
    }

    // Created, lapsed and discarded are the only authorization transitions; the reason is a code, never a message from the token service.
    public async Task AuthorizationAsync(string transition, string requestId, string? jobId, string actor, string? reason, int itemsFailed = 0, string? correlationId = null)
    {
        var safeTransition = SafeDimension(transition);
        var safeReason = SafeDimension(reason);
        logger.LogInformation(
            "Bulk authorization {Transition} {CorrelationId} {RequestId} {JobId} {Stage} {ReasonCode} {ItemsFailed}",
            safeTransition, correlationId, requestId, jobId, "authorization", safeReason, itemsFailed);
        await WriteAsync(new ProcessingEvent
        {
            Actor = actor,
            EventType = $"bulk-scoring-authorization.{safeTransition}",
            EntityType = "BulkRequestAuthorization",
            EntityId = requestId,
            CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
            Timestamp = Now,
            PayloadJson = JsonSerializer.Serialize(new
            {
                factVersion = 1,
                jobId,
                reasonCode = safeReason,
                itemsFailed,
            }),
        });
    }

    private static string SafeDimension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var candidate = value.Trim().ToLowerInvariant();
        return Regex.IsMatch(candidate, "^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)
            ? candidate
            : "unspecified";
    }

    private void Log(BulkScoringRequest request, string? itemId, string stage, int attempt, string? reason, long durationMs, string message) =>
        logger.LogInformation(
            "{Message} {CorrelationId} {RequestId} {ItemId} {JobId} {Stage} {Attempt} {ReasonCode} {DurationMs}",
            message, request.CorrelationId, request.Id, itemId, request.JobId, stage, attempt, reason, durationMs);

    // The item is already saved; losing an audit write must not undo or repeat the work, so it is reported loudly instead.
    private async Task WriteAsync(ProcessingEvent evt)
    {
        try
        {
            await events.AddAsync(evt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit entry {EventType} for {EntityId} could not be written", evt.EventType, evt.EntityId);
        }
    }
}
