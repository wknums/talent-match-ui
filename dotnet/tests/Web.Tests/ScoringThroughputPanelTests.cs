using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class ScoringThroughputPanelTests : BunitContext
{
    private static readonly DateTimeOffset AsOf = new(2026, 9, 17, 8, 47, 12, TimeSpan.Zero);

    [Fact]
    public async Task Api_UsesTypedUnfilteredThroughputEndpoint()
    {
        var expected = Snapshot(Enumerable.Range(1, 24).ToArray());
        using var handler = new RecordingHandler((_, _) => Task.FromResult(Response(expected)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var cancellation = new CancellationTokenSource();

        var result = await new ApiClient(http).GetScoringThroughputAsync(cancellation.Token);

        handler.Method.Should().Be(HttpMethod.Get);
        handler.RequestUri!.PathAndQuery.Should().Be("/api/stats/scoring-throughput");
        handler.RequestToken.CanBeCanceled.Should().BeTrue();
        result.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        result.Hours[0].StartUtc.Should().Be(AsOf.AddHours(-24));
        result.Hours[^1].StartUtc.Should().Be(AsOf.AddHours(-1));
        result.Hours[^1].EndUtc.Should().Be(AsOf);
    }

    [Fact]
    public async Task Api_PreservesAuthorizationErrors()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"You do not have access to these statistics.","error":"forbidden"}""")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        var action = () => new ApiClient(http).GetScoringThroughputAsync();

        var exception = (await action.Should().ThrowAsync<ApiException>()).Which;
        exception.StatusCode.Should().Be(403);
        exception.ErrorCode.Should().Be("forbidden");
        exception.Message.Should().Be("You do not have access to these statistics.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"asOfUtc":"2026-09-17T08:47:12Z","scoredLastHour":0,"scoredLast24Hours":0,"hours":[]}""")]
    public async Task Api_RejectsEmptyOrMalformedResponsesInsteadOfReturningZeros(string body)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        var action = () => new ApiClient(http).GetScoringThroughputAsync();

        await action.Should().ThrowAsync<JsonException>();
    }

    [Theory]
    [InlineData("missing-total")]
    [InlineData("missing-count")]
    [InlineData("null-bucket")]
    [InlineData("negative-count")]
    [InlineData("wrong-total")]
    [InlineData("wrong-headline")]
    [InlineData("wrong-window")]
    public async Task Api_RejectsInvalidBucketsAndTotals(string invalidCase)
    {
        var json = JsonSerializer.SerializeToNode(Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var hours = json["hours"]!.AsArray();
        switch (invalidCase)
        {
            case "missing-total": json.AsObject().Remove("scoredLast24Hours"); break;
            case "missing-count": hours[0]!.AsObject().Remove("count"); break;
            case "null-bucket": hours[0] = null; break;
            case "negative-count": hours[0]!["count"] = -1; break;
            case "wrong-total": json["scoredLast24Hours"] = 5; break;
            case "wrong-headline": json["scoredLastHour"] = 5; break;
            case "wrong-window": hours[23]!["startUtc"] = AsOf.AddHours(-2); break;
        }
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json.ToJsonString())
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        var action = () => new ApiClient(http).GetScoringThroughputAsync();

        await action.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public void Panel_RendersDefaultOpenCollapsibleLineGraphWithAccessibleHourlyDetails()
    {
        var expected = Snapshot(Enumerable.Range(1, 24).ToArray());
        Register((_, _) => Task.FromResult(Response(expected)));

        var cut = Render<ScoringThroughputPanel>();

        cut.WaitForAssertion(() => cut.FindAll("circle.throughput-point").Count.Should().Be(24));
        var toggle = cut.Find("button.throughput-toggle");
        toggle.GetAttribute("aria-expanded").Should().Be("true");
        toggle.GetAttribute("aria-label").Should().Be("Collapse dashboard performance");
        toggle.QuerySelector(".disclosure-chevron").Should().NotBeNull();
        cut.Find(".throughput-metric").TextContent.Should().Be("24");
        cut.Find("h4").TextContent.Should().Be("Applications scored / hour");
        cut.Markup.Should().Contain("Rolling last 60 minutes");
        cut.Find(".throughput-total").TextContent.Should().Contain("300");
        cut.FindAll("polyline.throughput-line").Should().ContainSingle();
        var points = cut.FindAll("circle.throughput-point");
        points.Select(point => int.Parse(point.GetAttribute("data-count")!))
            .Should().Equal(Enumerable.Range(1, 24));
        cut.FindAll(".current-point").Should().ContainSingle().Which.Should().BeSameAs(points[^1]);
        points[^1].GetAttribute("data-current").Should().Be("true");
        points[^1].GetAttribute("cy").Should().Be("72");
        cut.FindAll(".throughput-bucket title").Should().HaveCount(24);
        cut.Find("svg title").TextContent.Should().Contain("rolling last 24 hours");
        cut.Find("svg desc").TextContent.Should().Contain("first persisted aggregate score");
        cut.FindAll(".throughput-bucket").Should().OnlyContain(bucket => bucket.GetAttribute("tabindex") == "0");
        cut.FindAll("tbody tr").Should().HaveCount(24);
        cut.FindAll("tbody tr")[23].TextContent.Should().Contain("24");
        cut.Find(".throughput-updated time").GetAttribute("datetime").Should().Be(AsOf.ToString("O"));
        cut.Find(".throughput-updated time").TextContent.Should()
            .Be(AsOf.ToLocalTime().ToString("MMM d, yyyy HH:mm:ss zzz"));
        cut.FindAll(".x-tick text")[^1].TextContent.Should().Be(AsOf.AddHours(-1).ToLocalTime().ToString("HH:mm"));
        cut.Find(".throughput-scope").TextContent.Should().Contain("regardless of the job-list filters")
            .And.Contain("scoring runs, retries, reaggregation and test applications");

        toggle.Click();
        cut.Find("button.throughput-toggle").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("button.throughput-toggle").GetAttribute("aria-label").Should().Be("Expand dashboard performance");
        cut.FindAll("svg.throughput-chart").Should().BeEmpty();
    }

    [Fact]
    public void Panel_ZeroHistoryIsRealDataWith24BaselinePointsAndIntegerAxis()
    {
        Register((_, _) => Task.FromResult(Response(Snapshot())));

        var cut = Render<ScoringThroughputPanel>();

        cut.WaitForAssertion(() => cut.FindAll("circle.throughput-point").Count.Should().Be(24));
        cut.Find(".throughput-metric").TextContent.Should().Be("0");
        cut.Find(".throughput-empty").TextContent.Should().Contain("No applications were scored");
        cut.FindAll("circle.throughput-point").Should().OnlyContain(point =>
            point.GetAttribute("data-count") == "0" && point.GetAttribute("cy") == "216");
        cut.FindAll(".y-tick text").Select(tick => tick.TextContent).Should().Equal("0", "1");
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    [Fact]
    public void Panel_LargeCountKeepsIntegerTicksAndInvariantSvgCoordinates()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var counts = new int[24];
            counts[^1] = int.MaxValue;
            Register((_, _) => Task.FromResult(Response(Snapshot(counts))));

            var cut = Render<ScoringThroughputPanel>();

            cut.WaitForAssertion(() => cut.FindAll("circle.throughput-point").Count.Should().Be(24));
            var ticks = cut.FindAll(".y-tick text")
                .Select(tick => long.Parse(tick.TextContent, NumberStyles.Number)).ToArray();
            ticks.Should().HaveCount(4).And.BeInAscendingOrder();
            ticks[^1].Should().BeGreaterThanOrEqualTo(int.MaxValue);
            var point = cut.Find(".current-point");
            point.GetAttribute("cy").Should().NotContain(",");
            double.Parse(point.GetAttribute("cy")!, CultureInfo.InvariantCulture).Should().BeInRange(36, 216);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task Panel_LoadingAndInitialErrorNeverInventZeroData()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Register((_, token) => completion.Task.WaitAsync(token));
        var cut = Render<ScoringThroughputPanel>();

        cut.Find(".throughput-loading").TextContent.Should().Contain("Loading");
        cut.FindAll(".throughput-metric, .throughput-point, .throughput-empty").Should().BeEmpty();
        await cut.InvokeAsync(() => completion.SetResult(Failure()));

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("Scoring throughput unavailable"));
        cut.Find("[role=alert]").TextContent.Should().Contain("Statistics temporarily unavailable");
        cut.FindAll(".throughput-metric, .throughput-point, .throughput-empty, .throughput-updated").Should().BeEmpty();
    }

    [Fact]
    public async Task Panel_RefreshFailureKeepsSnapshotAsStaleThenRecovers()
    {
        var original = Snapshot(Enumerable.Repeat(2, 24).ToArray());
        var recovered = Snapshot(Enumerable.Repeat(3, 24).ToArray(), AsOf.AddSeconds(20));
        var calls = 0;
        var (_, clock) = Register((_, _) => Task.FromResult(++calls switch
        {
            1 => Response(original),
            2 => Failure(),
            _ => Response(recovered)
        }));
        var cut = Render<ScoringThroughputPanel>();
        cut.WaitForAssertion(() => cut.Find(".throughput-metric").TextContent.Should().Be("2"));

        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(10)));

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("Stale data"));
        cut.Find(".throughput-metric").TextContent.Should().Be("2");
        cut.FindAll(".throughput-point").Should().HaveCount(24)
            .And.OnlyContain(point => point.GetAttribute("data-count") == "2");
        cut.Find(".throughput-updated").TextContent.Should().Contain("Last successful update:");
        cut.Find(".throughput-updated time").GetAttribute("datetime").Should().Be(AsOf.ToString("O"));

        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(10)));

        cut.WaitForAssertion(() => cut.Find(".throughput-metric").TextContent.Should().Be("3"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
        cut.Find(".throughput-updated time").GetAttribute("datetime").Should().Be(recovered.AsOfUtc.ToString("O"));
    }

    [Fact]
    public async Task Panel_RefreshIsEvery10SecondsNonoverlappingAndCancelledOnDispose()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var (handler, clock) = Register((_, token) => ++calls == 1
            ? Task.FromResult(Response(Snapshot()))
            : completion.Task.WaitAsync(token));
        var cut = Render<ScoringThroughputPanel>();
        cut.WaitForAssertion(() => clock.ActiveTimers.Should().Be(1));

        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(9)));
        handler.RequestCount.Should().Be(1);
        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(1)));
        cut.WaitForAssertion(() => handler.RequestCount.Should().Be(2));
        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(30)));
        handler.RequestCount.Should().Be(2);
        handler.MaximumConcurrency.Should().Be(1);

        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());

        handler.RequestToken.IsCancellationRequested.Should().BeTrue();
        handler.ActiveRequests.Should().Be(0);
        clock.ActiveTimers.Should().Be(0);
        clock.Advance(TimeSpan.FromMinutes(1));
        handler.RequestCount.Should().Be(2);
        await cut.Instance.DisposeAsync();
    }

    [Fact]
    public async Task Panel_DisposalDuringInitialLoadCancelsRequestWithoutStartingTimer()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (handler, clock) = Register((_, token) => completion.Task.WaitAsync(token));
        var cut = Render<ScoringThroughputPanel>();

        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());

        cut.WaitForAssertion(() => handler.ActiveRequests.Should().Be(0));
        handler.RequestToken.IsCancellationRequested.Should().BeTrue();
        clock.ActiveTimers.Should().Be(0);
        clock.Advance(TimeSpan.FromMinutes(1));
        handler.RequestCount.Should().Be(1);
    }

    private (RecordingHandler Handler, ManualTimeProvider Clock) Register(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
    {
        var handler = new RecordingHandler(response);
        var clock = new ManualTimeProvider();
        Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        Services.AddSingleton<ApiClient>();
        Services.AddSingleton<TimeProvider>(clock);
        return (handler, clock);
    }

    private static ScoringThroughputDto Snapshot(int[]? counts = null, DateTimeOffset? asOf = null)
    {
        counts ??= new int[24];
        var end = asOf ?? AsOf;
        return new(end, counts[^1], counts.Sum(), counts.Select((count, index) =>
            new ScoringThroughputBucketDto(end.AddHours(index - 24), end.AddHours(index - 23), count)).ToList());
    }

    private static HttpResponseMessage Response(ScoringThroughputDto snapshot)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) };

    private static HttpResponseMessage Failure()
        => new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"message":"Statistics temporarily unavailable"}""")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public CancellationToken RequestToken { get; private set; }
        public int RequestCount { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public int ActiveRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            RequestToken = cancellationToken;
            RequestCount++;
            ActiveRequests++;
            MaximumConcurrency = Math.Max(MaximumConcurrency, ActiveRequests);
            try
            {
                return await response(request, cancellationToken);
            }
            finally
            {
                ActiveRequests--;
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = AsOf;
        public int ActiveTimers => timers.Count(timer => !timer.Disposed);

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            foreach (var timer in timers.ToArray())
                timer.Fire(now);
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset next;
            private TimeSpan period;
            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                if (Disposed)
                    return false;
                next = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + dueTime;
                period = newPeriod;
                return true;
            }

            public void Fire(DateTimeOffset target)
            {
                while (!Disposed && next <= target)
                {
                    next = period > TimeSpan.Zero ? next + period : DateTimeOffset.MaxValue;
                    callback(state);
                }
            }

            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
