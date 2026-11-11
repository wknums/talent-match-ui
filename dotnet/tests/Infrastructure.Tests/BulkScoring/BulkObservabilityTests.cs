using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.SharePoint.Authorization;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringWorkerTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkObservabilityTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";

    [Fact]
    public async Task Every_item_and_request_transition_the_worker_makes_has_an_explicit_service_actor()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: false);
        await host.StartWorkerAsync();
        await Eventually(async () => (await host.RequestAsync(requestId)).ScoringCount == 9, "files reach scoring");
        await host.Db.Applications.ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Completed"));
        await Eventually(async () => (await host.RequestAsync(requestId)).Status == BulkRequestStatus.Completed, "the request completes");
        await Eventually(async () => await host.Db.ProcessingEvents.AnyAsync(e => e.EventType == "bulk-scoring-authorization.discarded"), "the discard is audited");

        var events = await host.Db.ProcessingEvents.AsNoTracking().Where(e => e.EventType.StartsWith("bulk-scoring")).ToListAsync();
        events.Count(e => e.EventType == "bulk-scoring-item.handed_off").Should().Be(9);
        events.Count(e => e.EventType == "bulk-scoring-item.skipped").Should().Be(2);
        events.Count(e => e.EventType == "bulk-scoring-item.completed").Should().Be(9);
        events.Count(e => e.EventType == "bulk-scoring-request.completed").Should().Be(1);
        events.Count(e => e.EventType == "bulk-scoring-authorization.discarded").Should().Be(1);
        events.Where(e => e.EventType != "bulk-scoring-authorization.discarded")
            .Should().OnlyContain(e => e.Actor == "system");
        events.Single(e => e.EventType == "bulk-scoring-authorization.discarded").Actor
            .Should().Be("bulk-authorization-timer");
        events.Should().OnlyContain(e => e.CorrelationId.Length > 0 && e.Timestamp > DateTime.UnixEpoch);
        events.Where(e => e.EntityType == nameof(BulkScoringItem)).Select(e => e.EntityId).Distinct().Should().HaveCount(11, "each item is audited by its own identifier");

        var completed = events.Single(e => e.EventType == "bulk-scoring-request.completed");
        completed.EntityId.Should().Be(requestId);
        JsonDocument.Parse(completed.PayloadJson).RootElement.GetProperty("outcome").GetString().Should().Be("all_succeeded");
        var handed = events.First(e => e.EventType == "bulk-scoring-item.handed_off");
        var payload = JsonDocument.Parse(handed.PayloadJson).RootElement;
        payload.GetProperty("requestId").GetString().Should().Be(requestId);
        payload.GetProperty("jobId").GetString().Should().Be("job-1");
        payload.GetProperty("stage").GetString().Should().Be("hand_off");
    }

    [Fact]
    public async Task Audit_entries_never_hold_file_names_content_or_token_material()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: false);
        await host.StartWorkerAsync();
        await Eventually(async () => (await host.RequestAsync(requestId)).ScoringCount == 9, "files reach scoring");
        await host.StopWorkerAsync();

        var names = await host.Db.BulkScoringItems.AsNoTracking().Select(i => i.FileName).ToListAsync();
        var payloads = string.Join("\n", await host.Db.ProcessingEvents.AsNoTracking().Select(e => e.PayloadJson + e.EntityId + e.EventType).ToListAsync());

        payloads.Should().NotBeEmpty();
        foreach (var name in names) payloads.Should().NotContain(name);
        payloads.Should().NotContainEquivalentOf("user-assertion").And.NotContainEquivalentOf("bearer").And.NotContainEquivalentOf("access_token");
    }

    [Fact]
    public async Task A_lapse_audits_the_lapse_with_its_reason_and_how_many_files_it_failed()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        host.Provider.GetRequiredService<MockAuthorizationControl>().Lapse(requestId, "interaction_required");
        await host.StartWorkerAsync();
        await Eventually(async () => (await host.RequestAsync(requestId)).Status == BulkRequestStatus.Completed, "the request ends");

        var lapsed = await host.Db.ProcessingEvents.AsNoTracking().SingleAsync(e => e.EventType == "bulk-scoring-authorization.lapsed");
        lapsed.Actor.Should().Be("system");
        lapsed.EntityId.Should().Be(requestId);
        var body = JsonDocument.Parse(lapsed.PayloadJson).RootElement;
        body.GetProperty("reasonCode").GetString().Should().Be("interaction_required");
        body.GetProperty("itemsFailed").GetInt32().Should().Be(11);
    }

    [Fact]
    public async Task Logs_carry_the_documented_fields_and_never_the_file_name()
    {
        var logger = new CapturingLogger();
        var telemetry = new BulkTelemetry(new NullEvents(), logger, TimeProvider.System);
        var request = new BulkScoringRequest { Id = "req-9", JobId = "job-9", CorrelationId = "corr-9" };
        var item = new BulkScoringItem { Id = "item-9", RequestId = "req-9", FileName = "Jane Secret CV.pdf", Attempt = 2, ReasonCode = BulkReasonCode.SourceChanged };

        await telemetry.ItemAsync(request, item, "failed", 125);

        var state = logger.Entries.Should().ContainSingle().Which;
        foreach (var key in new[] { "CorrelationId", "RequestId", "ItemId", "JobId", "Stage", "Attempt", "ReasonCode", "DurationMs" })
            state.Should().ContainKey(key);
        state["CorrelationId"].Should().Be("corr-9");
        state["RequestId"].Should().Be("req-9");
        state["ItemId"].Should().Be("item-9");
        state["JobId"].Should().Be("job-9");
        state["Stage"].Should().Be("import");
        state["Attempt"].Should().Be(2);
        state["ReasonCode"].Should().Be("source_changed");
        state["DurationMs"].Should().Be(125L);
        logger.Text.Should().NotContain("Jane").And.NotContain("Secret");
    }

    [Fact]
    public async Task Metrics_count_items_by_outcome_and_reason_and_throttling_separately()
    {
        var measurements = new List<(string Name, long Value, string Tags)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BulkTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))));
        });
        listener.Start();

        var telemetry = new BulkTelemetry(new NullEvents(), new CapturingLogger(), TimeProvider.System);
        var request = new BulkScoringRequest { Id = "r", JobId = "j" };
        var item = new BulkScoringItem { Id = "i", ReasonCode = BulkReasonCode.AccessDenied };
        await telemetry.ItemAsync(request, item, "failed", 1);
        telemetry.ItemRetrying(request, item, TimeSpan.FromSeconds(30), 1);
        telemetry.ItemRetrying(request, item, null, 1);

        lock (measurements)
        {
            measurements.Should().Contain(m => m.Name == "bulk.items" && m.Tags == "outcome=failed,reason=access_denied");
            measurements.Count(m => m.Name == "bulk.item.retries").Should().Be(2);
            measurements.Count(m => m.Name == "bulk.source.throttled").Should().Be(1, "only a retry that carried a Retry-After is a throttle");
        }
    }

    [Theory]
    [InlineData("application/pdf", "pdf")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "word")]
    [InlineData("application/msword", "word")]
    [InlineData("text/markdown", "markdown")]
    [InlineData("text/plain", "text")]
    [InlineData("image/png", "image")]
    [InlineData("application/zip", "other")]
    [InlineData(null, "other")]
    public void Files_are_grouped_into_a_small_fixed_set_of_kinds(string? mime, string expected)
        => BulkTelemetry.KindOf(mime).Should().Be(expected);

    [Fact]
    public void A_phase_is_logged_and_measured_with_identifiers_and_codes_only()
    {
        var logger = new CapturingLogger();
        var telemetry = new BulkTelemetry(new NullEvents(), logger, TimeProvider.System);
        var request = new BulkScoringRequest { Id = "req-7", JobId = "job-7", CorrelationId = "corr-7" };
        var item = new BulkScoringItem { Id = "item-7", RequestId = "req-7", FileName = "Jane Secret CV.docx", Attempt = 1 };
        var measurements = ListenForPhaseDurations(out var listener);
        using var _ = listener;

        telemetry.Phase(request, item, BulkTelemetry.Phases.SourcePdf, "word", "ok", 1234);

        var state = logger.Entries.Should().ContainSingle().Which;
        foreach (var key in new[] { "Phase", "CorrelationId", "RequestId", "ItemId", "JobId", "Attempt", "Kind", "Outcome", "DurationMs" })
            state.Should().ContainKey(key);
        state["Phase"].Should().Be("source_pdf");
        state["Kind"].Should().Be("word");
        state["Outcome"].Should().Be("ok");
        state["DurationMs"].Should().Be(1234L);
        logger.Text.Should().StartWith("bulk item phase completed").And.NotContain("Jane").And.NotContain("Secret");
        lock (measurements)
            measurements.Should().Contain(m => m.Value == 1234 && m.Tags == "phase=source_pdf,kind=word,outcome=ok");
    }

    [Fact]
    public void A_rasterize_phase_reports_pages_output_size_resolution_and_a_second_pass()
    {
        var logger = new CapturingLogger();
        var telemetry = new BulkTelemetry(new NullEvents(), logger, TimeProvider.System);
        var request = new BulkScoringRequest { Id = "req-8", JobId = "job-8", CorrelationId = "corr-8" };
        var item = new BulkScoringItem { Id = "item-8", RequestId = "req-8", FileName = "Another Secret.pdf", Attempt = 1 };
        var durations = ListenForPhaseDurations(out var listener);
        using var _ = listener;

        telemetry.Rasterized(request, item, "pdf", "ok", 4321, pages: 3, outputBytes: 987_654, dpi: 120, steppedDown: true);

        var state = logger.Entries.Should().ContainSingle().Which;
        state["Phase"].Should().Be("rasterize");
        state["Pages"].Should().Be(3);
        state["OutputBytes"].Should().Be(987_654L);
        state["Dpi"].Should().Be(120);
        state["SteppedDown"].Should().Be(true);
        state["DurationMs"].Should().Be(4321L);
        logger.Text.Should().NotContain("Another").And.NotContain("Secret");
        lock (durations)
            durations.Should().Contain(m => m.Value == 4321 && m.Tags == "phase=rasterize,kind=pdf,outcome=ok");
    }

    [Fact]
    public async Task A_converted_request_records_a_download_and_a_rasterize_duration_for_every_file_it_processes()
    {
        var durations = ListenForPhaseDurations(out var listener);
        using var _ = listener;
        await using var host = await WorkerHost.CreateAsync();
        await host.ConfirmAsync(Candidates, allowDuplicates: true, convertToRasterPdf: true);

        var (processed, outcome) = await host.ProcessOneAsync("bulk-timing-worker");

        outcome.Should().Be(BulkItemOutcome.HandedOff);
        processed.ConvertedStoredAt.Should().NotBeNull();
        var kind = BulkTelemetry.KindOf(processed.MimeType);
        // The meter is process-wide and other tests run in parallel, so only presence is asserted.
        lock (durations)
        {
            durations.Should().Contain(m => m.Tags == $"phase=download,kind={kind},outcome=ok");
            durations.Should().Contain(m => m.Tags == $"phase=rasterize,kind={kind},outcome=ok");
        }
    }

    private static List<(string Name, double Value, string Tags)> ListenForPhaseDurations(out System.Diagnostics.Metrics.MeterListener listener)
    {
        var measurements = new List<(string Name, double Value, string Tags)>();
        var created = new System.Diagnostics.Metrics.MeterListener();
        created.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BulkTelemetry.MeterName && instrument.Name == "bulk.item.phase.duration")
                l.EnableMeasurementEvents(instrument);
        };
        created.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))));
        });
        created.Start();
        listener = created;
        return measurements;
    }

    private sealed class NullEvents : IProcessingEventRepository
    {
        public Task AddAsync(ProcessingEvent evt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddAuthorizationEventAsync(string actor, string action, string subjectId, IReadOnlyDictionary<string, object?> safeDetails, string correlationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProcessingEvent>> GetFilteredAsync(string? entityType = null, string? eventType = null, DateTime? startDate = null, DateTime? endDate = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessingEvent>>([]);
    }

    private sealed class CapturingLogger : ILogger<BulkTelemetry>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];
        public string Text { get; private set; } = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                Entries.Add(pairs.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value));
            Text += formatter(state, exception) + "\n";
        }
    }
}
