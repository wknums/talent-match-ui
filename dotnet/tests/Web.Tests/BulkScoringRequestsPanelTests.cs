using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class BulkScoringRequestsPanelTests : IDisposable
{
    [Fact]
    public void Each_request_shows_its_counts_and_the_item_table_total_matches_them()
    {
        var handler = new PanelHandler { Requests = [Summary(counts: Counts(discovered: 3, completed: 1, failed: 1, waiting: 1))], Items = Items(3) };
        var cut = OpenPanel(handler);

        cut.Find("[data-testid=request-counts]").TextContent.Should().Contain("3 files").And.Contain("1 scored").And.Contain("1 failed").And.Contain("1 waiting");
        cut.Find("[role=progressbar]").GetAttribute("aria-valuenow").Should().Be("2");
        cut.Find("[data-testid=details]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=item-row]").Should().HaveCount(3));
        cut.Find("[data-testid=item-range]").TextContent.Should().Be("Showing 1–3 of 3");
    }

    [Fact]
    public void A_request_detail_failure_remains_visible_when_its_item_page_loads_successfully()
    {
        var handler = new PanelHandler
        {
            Requests = [Summary(counts: Counts(discovered: 3, waiting: 3))],
            Items = Items(3),
        };
        var cut = OpenPanel(handler);
        handler.DetailStatus = HttpStatusCode.ServiceUnavailable;

        cut.Find("[data-testid=details]").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid=item-row]").Should().HaveCount(3);
            cut.FindAll("[role=alert]").Should().Contain(alert => alert.TextContent.Contains("Request details unavailable"));
        });
    }

    [Fact]
    public void Monitoring_shows_every_aggregate_count_including_excluded_and_unreadable_entries()
    {
        var cut = OpenPanel(new PanelHandler
        {
            Requests =
            [
                Summary(counts: Counts(
                    discovered: 11,
                    excluded: 2,
                    unreadable: 1,
                    waiting: 1,
                    importing: 1,
                    converting: 1,
                    scoring: 1,
                    completed: 1,
                    failed: 1,
                    skipped: 1,
                    cancelled: 1)),
            ],
        });

        cut.Find("[data-testid=request-counts]").TextContent.Should()
            .Contain("11 files")
            .And.Contain("2 excluded")
            .And.Contain("1 unreadable")
            .And.Contain("1 waiting")
            .And.Contain("1 importing")
            .And.Contain("1 converting")
            .And.Contain("1 being scored")
            .And.Contain("1 scored")
            .And.Contain("1 failed")
            .And.Contain("1 skipped")
            .And.Contain("1 cancelled");
    }

    [Fact]
    public void The_toggle_shows_how_many_requests_the_job_has_and_an_empty_job_says_so()
    {
        var cut = OpenPanel(new PanelHandler(), expand: false);

        cut.Find(".bulk-panel-toggle").TextContent.Trim().Should().Be("Bulk scoring requests (0)");
        cut.Find(".bulk-panel-toggle").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".bulk-panel-toggle").Click();
        cut.Markup.Should().Contain("No bulk scoring requests yet");
    }

    [Fact]
    public void Collapsed_pipeline_summary_surfaces_pre_application_failures()
    {
        var cut = OpenPanel(new PanelHandler
        {
            Requests =
            [
                Summary(
                    status: "completed",
                    outcome: "failed",
                    counts: Counts(discovered: 68, failed: 68)),
            ],
        }, expand: false);

        var summary = cut.Find("[data-testid=bulk-pipeline-status]");
        summary.GetAttribute("role").Should().Be("alert");
        summary.TextContent.Should().Contain("finished")
            .And.Contain("68 failed");
    }

    [Fact]
    public void Collapsed_pipeline_summary_shows_live_bulk_processing_stages()
    {
        var cut = OpenPanel(new PanelHandler
        {
            Requests =
            [
                Summary(counts: Counts(
                    discovered: 12,
                    waiting: 3,
                    importing: 2,
                    converting: 1,
                    scoring: 4,
                    completed: 2)),
            ],
        }, expand: false);

        cut.Find("[data-testid=bulk-pipeline-status]").TextContent.Should()
            .Contain("3 waiting")
            .And.Contain("2 importing")
            .And.Contain("1 converting")
            .And.Contain("4 being scored")
            .And.Contain("2 scored");
    }

    [Fact]
    public void Items_are_paged_on_the_server_beyond_100_rows()
    {
        var handler = new PanelHandler { Requests = [Summary(counts: Counts(discovered: 250, waiting: 250))], Items = Items(250) };
        var cut = OpenPanel(handler);
        cut.Find("[data-testid=details]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=item-range]").TextContent.Should().Be("Showing 1–50 of 250"));
        cut.FindAll("[data-testid=item-row]").Should().HaveCount(50);
        Pager(cut, "Previous").HasAttribute("disabled").Should().BeTrue();

        Pager(cut, "Next").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=item-range]").TextContent.Should().Be("Showing 51–100 of 250"));
        cut.Markup.Should().Contain("cv-51.pdf").And.NotContain("cv-1.pdf");
        handler.Calls.Should().Contain(c => c.Path.Contains("/items?page=2&pageSize=50"));

        for (var i = 0; i < 3; i++)
        {
            var expected = $"Showing {(i + 2) * 50 + 1}–{(i + 3) * 50} of 250";
            Pager(cut, "Next").Click();
            cut.WaitForAssertion(() => cut.Find("[data-testid=item-range]").TextContent.Should().Be(expected));
        }
        Pager(cut, "Next").HasAttribute("disabled").Should().BeTrue("the last page is page 5");
    }

    [Fact]
    public void Status_and_name_filters_are_sent_to_the_server_and_restart_at_page_one()
    {
        var handler = new PanelHandler { Requests = [Summary(counts: Counts(discovered: 250, waiting: 250))], Items = Items(250) };
        var cut = OpenPanel(handler);
        cut.Find("[data-testid=details]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=item-row]").Should().NotBeEmpty());
        Pager(cut, "Next").Click();

        cut.Find("[data-testid=status-filter]").Change("failed");
        cut.Find("[data-testid=name-filter]").Change("alice");

        cut.WaitForAssertion(() => handler.Calls.Should().Contain(c => c.Path.Contains("page=1") && c.Path.Contains("status=failed") && c.Path.Contains("q=alice")));
        cut.FindAll("[data-testid=status-filter] option").Select(o => o.GetAttribute("value")).Should().NotContain("excluded");
    }

    [Fact]
    public void A_failed_item_shows_its_reason_and_unfinished_items_show_none()
    {
        var handler = new PanelHandler
        {
            Requests = [Summary(counts: Counts(discovered: 4, failed: 3, waiting: 1))],
            Items = _ => new BulkItemPageDto(
            [
                Item("a.pdf", "failed", "source_changed", "The file changed after it was discovered."),
                Item("b.pdf", "failed", "authorization_lapsed", null),
                Item("c.pdf", "cancelled", "cancelled_by_user", null),
                Item("d.pdf", "waiting", null, null),
            ], 1, 50, 4),
        };
        var cut = OpenPanel(handler);
        cut.Find("[data-testid=details]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=item-reason]").Should().HaveCount(4));
        var reasons = cut.FindAll("[data-testid=item-reason]").Select(r => r.TextContent).ToList();
        reasons[0].Should().Be("The file changed after it was discovered.");
        reasons[1].Should().Contain("Sign in again");
        reasons[2].Should().Contain("Cancelled");
        reasons[3].Should().BeEmpty();
    }

    [Fact]
    public void Cancel_asks_first_and_says_that_files_already_being_scored_will_finish()
    {
        var handler = new PanelHandler { Requests = [Summary(status: "active", counts: Counts(discovered: 10, waiting: 8, scoring: 2))] };
        var cut = OpenPanel(handler);

        cut.Find("[data-testid=cancel]").Click();

        cut.Find("[data-testid=cancel-copy]").TextContent.Should().Contain("Files already being scored will finish");
        handler.Calls.Should().NotContain(c => c.Path.EndsWith("/cancel"), "nothing is sent until it is confirmed");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Yes, cancel").Click();

        cut.WaitForAssertion(() => handler.Calls.Should().Contain(c => c.Method == "POST" && c.Path.EndsWith("/bulk-scoring-requests/req-1/cancel")));
        handler.Calls.Single(c => c.Path.EndsWith("/cancel")).Body.Should().Contain("\"expectedConcurrencyVersion\":4");
    }

    [Fact]
    public void Cancel_shows_a_loading_state_and_then_the_cancelling_note_with_how_many_are_still_scoring()
    {
        var gate = new TaskCompletionSource();
        var handler = new PanelHandler { Requests = [Summary(status: "active", counts: Counts(discovered: 10, waiting: 8, scoring: 2))], CancelGate = gate.Task };
        var cut = OpenPanel(handler);
        cut.Find("[data-testid=cancel]").Click();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Yes, cancel").Click();

        cut.WaitForAssertion(() =>
        {
            var busy = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancelling…");
            busy.HasAttribute("disabled").Should().BeTrue();
        });

        handler.AfterCancel = Summary(status: "active", counts: Counts(discovered: 10, cancelled: 8, scoring: 2), cancelRequested: true);
        gate.SetResult();

        cut.WaitForAssertion(() => cut.Find("[data-testid=cancelling]").TextContent.Should().Contain("2 files already being scored will finish"));
        cut.FindAll("[data-testid=cancel]").Should().BeEmpty("a cancelled request cannot be cancelled again");
    }

    [Fact]
    public void A_cancel_that_conflicts_says_the_request_changed_and_reloads()
    {
        var handler = new PanelHandler
        {
            Requests = [Summary(status: "active", counts: Counts(discovered: 2, waiting: 2))],
            CancelResponse = () => Error(HttpStatusCode.Conflict, "version_conflict", "The request changed."),
        };
        var cut = OpenPanel(handler);
        cut.Find("[data-testid=cancel]").Click();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Yes, cancel").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=action-error]").TextContent.Should().Contain("changed"));
        cut.Find("[data-testid=action-error]").GetAttribute("role").Should().Be("alert");
        handler.Calls.Count(c => c.Method == "GET" && c.Path.StartsWith("/api/jobs/job-1/bulk-scoring-requests")).Should().BeGreaterThan(1, "the list is reloaded");
    }

    [Fact]
    public void Retry_is_offered_for_finished_requests_with_failed_or_cancelled_files_and_posts_to_the_retry_endpoint()
    {
        var handler = new PanelHandler { Requests = [Summary(status: "completed", outcome: "partial", counts: Counts(discovered: 5, completed: 2, failed: 2, cancelled: 1))] };
        var cut = OpenPanel(handler);

        cut.Find("[data-testid=retry]").TextContent.Trim().Should().Be("Retry failed");
        cut.Markup.Should().Contain("failed before scoring, and the cancelled ones").And.Contain("retried from the Failure Queue");
        cut.Find("[data-testid=retry]").Click();

        cut.WaitForAssertion(() => handler.Calls.Should().Contain(c => c.Method == "POST" && c.Path.EndsWith("/bulk-scoring-requests/req-1/retry-failed")));
    }

    [Fact]
    public void Retry_is_not_offered_when_nothing_failed_or_the_request_is_still_running_and_an_error_is_shown_when_it_is_refused()
    {
        var clean = OpenPanel(new PanelHandler { Requests = [Summary(status: "completed", outcome: "all_succeeded", counts: Counts(discovered: 2, completed: 2))] });
        clean.FindAll("[data-testid=retry]").Should().BeEmpty();

        var running = OpenPanel(new PanelHandler { Requests = [Summary(status: "active", counts: Counts(discovered: 2, waiting: 2))] });
        running.FindAll("[data-testid=retry]").Should().BeEmpty();

        var refused = OpenPanel(new PanelHandler
        {
            Requests = [Summary(status: "completed", outcome: "failed", counts: Counts(discovered: 1, failed: 1))],
            RetryResponse = () => Error(
                HttpStatusCode.UnprocessableEntity,
                "no_retryable_bulk_files",
                "No files from this request can be retried here. Files that failed while being scored are retried from the Failure Queue; otherwise start a new bulk scoring request."),
        });
        refused.Find("[data-testid=retry]").Click();
        refused.WaitForAssertion(() => refused.Find("[data-testid=action-error]").TextContent.Should()
            .Contain("retried from the Failure Queue"));
        refused.Find("[data-testid=retry]").NextElementSibling!.TextContent.Should().Contain("Failure Queue");
    }

    [Fact]
    public void Download_original_appears_only_for_items_that_kept_one_and_saves_the_file()
    {
        var handler = new PanelHandler
        {
            Requests = [Summary(counts: Counts(discovered: 2, completed: 2))],
            Items = _ => new BulkItemPageDto([Item("kept.docx", "completed", null, null, retained: true), Item("plain.txt", "completed", null, null)], 1, 50, 2),
            Original = [1, 2, 3],
        };
        var cut = OpenPanel(handler);
        Last.JSInterop.SetupVoid("talentMatchDownloadBase64", _ => true);
        cut.Find("[data-testid=details]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=item-row]").Should().HaveCount(2));
        cut.FindAll("[data-testid=download-original]").Should().ContainSingle();
        cut.FindAll("[data-testid=item-row]")[1].QuerySelector("[data-testid=download-original]").Should().BeNull();

        cut.Find("[data-testid=download-original]").Click();

        cut.WaitForAssertion(() => Last.JSInterop.VerifyInvoke("talentMatchDownloadBase64", 1));
        var saved = Last.JSInterop.Invocations["talentMatchDownloadBase64"].Single();
        saved.Arguments[0].Should().Be("kept.docx");
        saved.Arguments[1].Should().Be("application/octet-stream");
        saved.Arguments[2].Should().Be(Convert.ToBase64String([1, 2, 3]));
        handler.Calls.Should().Contain(c => c.Path.EndsWith("/bulk-scoring-requests/req-1/items/item-1/original"));
    }

    [Fact]
    public void The_configuration_changed_notice_appears_only_when_the_server_says_so()
    {
        var changed = OpenPanel(new PanelHandler { Requests = [Summary()], Detail = Detail(configChanged: true) });
        changed.Find("[data-testid=details]").Click();
        changed.WaitForAssertion(() => changed.Find("[data-testid=config-changed]").TextContent.Should().Contain("scoring configuration changed"));

        var same = OpenPanel(new PanelHandler { Requests = [Summary()], Detail = Detail(configChanged: false), Items = Items(3) });
        same.Find("[data-testid=details]").Click();
        same.WaitForAssertion(() => same.FindAll("[data-testid=item-row]").Should().NotBeEmpty());
        same.FindAll("[data-testid=config-changed]").Should().BeEmpty();
    }

    [Fact]
    public void A_lapsed_authorization_tells_the_recruiter_to_sign_in_again_and_use_retry_failed()
    {
        var cut = OpenPanel(new PanelHandler
        {
            Requests = [Summary(status: "completed", outcome: "failed", counts: Counts(discovered: 3, failed: 3), authorization: "lapsed")],
        });

        cut.Find("[data-testid=lapsed]").TextContent.Should().Contain("Sign in again").And.Contain("Retry failed");
        var cross = OpenPanel(new PanelHandler { Requests = [Summary(counts: Counts(discovered: 3, completed: 3), authorization: "not_applicable")] });
        cross.FindAll("[data-testid=lapsed]").Should().BeEmpty();
    }

    [Fact]
    public void Submitted_by_shows_whatever_name_the_server_resolved()
    {
        var cut = OpenPanel(new PanelHandler { Requests = [Summary(submittedBy: "former-employee-id")] });

        cut.Markup.Should().Contain("Submitted by former-employee-id");
    }

    [Fact]
    public void The_list_defaults_to_60_seconds_and_honors_an_explicit_interval()
    {
        BulkScoringRequestsPanel.DefaultRefreshInterval.Should().Be(TimeSpan.FromSeconds(60));

        var handler = new PanelHandler { Requests = [Summary(status: "active", counts: Counts(discovered: 4, waiting: 4))] };
        var cut = OpenPanel(handler, refresh: TimeSpan.FromMilliseconds(100));
        handler.Requests = [Summary(status: "active", counts: Counts(discovered: 4, completed: 3, waiting: 1))];

        cut.WaitForAssertion(() => cut.Find("[data-testid=request-counts]").TextContent.Should().Contain("3 scored"), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Status_colours_keep_a_text_contrast_of_at_least_4_5_to_1()
    {
        foreach (var status in BulkScoringStatusColors.Statuses)
        {
            var (foreground, background) = BulkScoringStatusColors.For(status);
            BulkScoringStatusColors.ContrastRatio(foreground, background).Should().BeGreaterThanOrEqualTo(BulkScoringStatusColors.MinimumContrast, status);
        }
    }

    [Fact]
    public void Status_badges_use_the_documented_colours()
    {
        var cut = OpenPanel(new PanelHandler { Requests = [Summary(status: "completed", outcome: "partial")] });

        var (foreground, background) = BulkScoringStatusColors.For("completed");
        cut.Find("[data-testid=request-status]").GetAttribute("style").Should().Contain(foreground).And.Contain(background);
    }

    [Fact]
    public void Polled_status_changes_are_announced_to_assistive_technology()
    {
        var cut = OpenPanel(new PanelHandler
        {
            Requests = [Summary(status: "active", counts: Counts(discovered: 2, waiting: 2))],
        });

        var status = cut.Find("[data-testid=request-status]");
        status.GetAttribute("aria-live").Should().Be("polite");
        status.GetAttribute("aria-atomic").Should().Be("true");
    }

    // ---- notification

    [Fact]
    public void The_notification_summarizes_the_outcome_links_to_the_job_and_stays_until_dismissed()
    {
        var handler = new PanelHandler { Notifications = [Summary(status: "completed", outcome: "partial", counts: Counts(discovered: 6, completed: 3, failed: 2, skipped: 1))] };
        var cut = OpenNotification(handler);

        cut.Find("[data-testid=bulk-notification]").TextContent.Should().Contain("Candidates").And.Contain("some files not scored");
        cut.Find("[data-testid=notification-counts]").TextContent.Should().Be("3 scored, 2 failed, 1 skipped.");
        cut.Find("[data-testid=notification-details]").GetAttribute("href").Should()
            .EndWith("/jobs/job-1?bulkRequestId=req-1");
        cut.FindAll("[data-testid=notification-lapsed]").Should().BeEmpty();

        cut.Find("[data-testid=notification-dismiss]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=bulk-notification]").Should().BeEmpty());
        handler.Calls.Should().Contain(c => c.Method == "POST" && c.Path.EndsWith("/bulk-scoring-requests/req-1/acknowledge"));
    }

    [Fact]
    public void The_terminal_notification_does_not_expose_source_authorization_state()
    {
        var cut = OpenNotification(new PanelHandler
        {
            Notifications = [Summary(status: "completed", outcome: "failed", counts: Counts(discovered: 3, failed: 3), authorization: "lapsed")],
        });

        cut.FindAll("[data-testid=notification-lapsed]").Should().BeEmpty(
            "FR-020 keeps the terminal notification limited to its frozen outcome and counts");
    }

    [Fact]
    public void The_notification_reads_the_same_in_both_tenant_modes()
    {
        string Render(string authorization)
        {
            var cut = OpenNotification(new PanelHandler { Notifications = [Summary(status: "completed", outcome: "all_succeeded", counts: Counts(discovered: 2, completed: 2), authorization: authorization)] });
            var text = cut.Find("[data-testid=bulk-notification]").TextContent;
            return Regex.Replace(text, "\\s+", " ").Trim();
        }

        Render("not_applicable").Should().Be(Render("active"));
    }

    [Fact]
    public void The_job_page_banner_shows_only_that_jobs_requests_and_the_dashboard_shows_all()
    {
        var notifications = new[] { Summary(jobId: "job-1"), Summary(jobId: "job-2", requestId: "req-2") };

        OpenNotification(new PanelHandler { Notifications = [.. notifications] }, jobId: "job-1")
            .FindAll("[data-testid=bulk-notification]").Should().ContainSingle();
        OpenNotification(new PanelHandler { Notifications = [.. notifications] }, jobId: null)
            .FindAll("[data-testid=bulk-notification]").Should().HaveCount(2);
    }

    [Fact]
    public void The_notification_is_silent_when_the_feature_is_off_or_the_call_fails()
    {
        var cut = OpenNotification(new PanelHandler { NotificationsStatus = HttpStatusCode.ServiceUnavailable });

        cut.Markup.Trim().Should().BeEmpty();
    }

    // ---- helpers

    private IRenderedComponent<BulkScoringRequestsPanel> OpenPanel(PanelHandler handler, bool expand = true, TimeSpan? refresh = null)
    {
        Register(handler);
        var cut = Last.Render<BulkScoringRequestsPanel>(p => p
            .Add(c => c.JobId, "job-1")
            .Add(c => c.RefreshInterval, refresh ?? TimeSpan.FromMinutes(10)));
        if (expand) cut.Find(".bulk-panel-toggle").Click();
        return cut;
    }

    private IRenderedComponent<BulkScoringRequestNotification> OpenNotification(PanelHandler handler, string? jobId = null)
    {
        Register(handler);
        return Last.Render<BulkScoringRequestNotification>(p => p.Add(c => c.JobId, jobId));
    }

    private readonly List<BunitContext> contexts = [];

    private BunitContext Last => contexts[^1];

    public void Dispose() => contexts.ForEach(c => c.Dispose());

    private void Register(PanelHandler handler)
    {
        contexts.Add(new BunitContext());
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        Last.Services.AddSingleton(api);
    }

    private static AngleSharp.Dom.IElement Pager(IRenderedComponent<BulkScoringRequestsPanel> cut, string label) =>
        cut.FindAll(".bulk-panel-pager button").Single(b => b.TextContent.Trim() == label);

    private static BulkCountsDto Counts(
        int discovered = 0,
        int excluded = 0,
        int unreadable = 0,
        int waiting = 0,
        int importing = 0,
        int converting = 0,
        int scoring = 0,
        int completed = 0,
        int failed = 0,
        int skipped = 0,
        int cancelled = 0) =>
        new(discovered, excluded, unreadable, waiting, importing, converting, scoring, completed, failed, skipped, cancelled);

    private static BulkRequestSummaryDto Summary(
        string status = "completed", string? outcome = "all_succeeded", BulkCountsDto? counts = null, string authorization = "not_applicable",
        string submittedBy = "Pat Recruiter", string jobId = "job-1", string requestId = "req-1", bool cancelRequested = false) =>
        new(requestId, jobId, status, status == "completed" ? outcome : null, "sharepoint", "Candidates", "/Candidates", submittedBy,
            DateTime.UtcNow.AddHours(-1), status == "completed" ? DateTime.UtcNow : null, false, false, cancelRequested, null, false,
            counts ?? Counts(discovered: 3, completed: 3), 4, authorization);

    private static BulkRequestDetailDto Detail(bool configChanged) =>
        new("req-1", "job-1", "completed", "all_succeeded", "sharepoint", "Candidates", "/Candidates", "https://x", "Pat Recruiter",
            DateTime.UtcNow, DateTime.UtcNow, false, false, false, null, false, Counts(discovered: 3, completed: 3), 4,
            new BulkLimitsDto(30_000, 8_388_608, 53_687_091_200, 30, 1_000), "config-1", configChanged, null, "same_tenant", "active", "tenant");

    private static BulkItemDto Item(string name, string status, string? reasonCode, string? reasonMessage, bool retained = false, int index = 1) =>
        new($"item-{index}", name, "/Candidates/" + name, 1_000, status, reasonCode, reasonMessage, 1, null, null, retained, false, "application/pdf", null, null, DateTime.UtcNow);

    private static Func<int, BulkItemPageDto> Items(int total) => page =>
        new BulkItemPageDto(
            Enumerable.Range((page - 1) * 50 + 1, Math.Max(0, Math.Min(50, total - (page - 1) * 50)))
                .Select(i => Item($"cv-{i}.pdf", "waiting", null, null, index: i)).ToList(),
            page, 50, total);

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status) { Content = JsonContent.Create(new { code, message }) };

    internal sealed record Call(string Method, string Path, string? Body);

    private sealed class PanelHandler : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];
        public List<BulkRequestSummaryDto> Requests { get; set; } = [];
        public List<BulkRequestSummaryDto> Notifications { get; set; } = [];
        public HttpStatusCode NotificationsStatus { get; set; } = HttpStatusCode.OK;
        public Func<int, BulkItemPageDto>? Items { get; set; }
        public BulkRequestDetailDto? Detail { get; set; }
        public HttpStatusCode DetailStatus { get; set; } = HttpStatusCode.OK;
        public Task? CancelGate { get; set; }
        public BulkRequestSummaryDto? AfterCancel { get; set; }
        public Func<HttpResponseMessage>? CancelResponse { get; set; }
        public Func<HttpResponseMessage>? RetryResponse { get; set; }
        public byte[] Original { get; set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Calls) Calls.Add(new Call(request.Method.Method, path, body));
            var route = request.RequestUri.AbsolutePath;

            if (request.Method == HttpMethod.Get && route == "/api/jobs/job-1/bulk-scoring-requests")
                return Json(new BulkRequestPageDto(Requests, 1, 20, Requests.Count));
            if (request.Method == HttpMethod.Get && route == "/api/bulk-scoring-requests/notifications")
                return NotificationsStatus == HttpStatusCode.OK ? Json(Notifications) : new HttpResponseMessage(NotificationsStatus);
            if (request.Method == HttpMethod.Get && route.EndsWith("/items"))
            {
                var page = int.Parse(Regex.Match(path, "page=(\\d+)").Groups[1].Value);
                return Json((Items ?? (_ => new BulkItemPageDto([], 1, 50, 0)))(page));
            }
            if (request.Method == HttpMethod.Get && route.EndsWith("/original"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Original) };
            if (request.Method == HttpMethod.Get && Regex.IsMatch(route, "^/api/bulk-scoring-requests/[^/]+$"))
                return DetailStatus == HttpStatusCode.OK
                    ? Json(Detail ?? BulkScoringRequestsPanelTests.Detail(false))
                    : Json(new { message = "Request details unavailable" }, DetailStatus);
            if (request.Method == HttpMethod.Post && route.EndsWith("/cancel"))
            {
                if (CancelGate is not null) await CancelGate;
                if (AfterCancel is not null) Requests = [AfterCancel];
                return CancelResponse?.Invoke() ?? Json(BulkScoringRequestsPanelTests.Detail(false), HttpStatusCode.Accepted);
            }
            if (request.Method == HttpMethod.Post && route.EndsWith("/retry-failed"))
                return RetryResponse?.Invoke() ?? Json(BulkScoringRequestsPanelTests.Detail(false), HttpStatusCode.Created);
            if (request.Method == HttpMethod.Post && route.EndsWith("/acknowledge"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    }
}
