using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class JobDetailLazyLoadingTests : BunitContext
{
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "JOB-20261020-111111")]
    [InlineData("22222222-2222-2222-2222-222222222222", "JOB-20261020-222222")]
    public void JobDetail_DisplaysTheCodeAndFullUniqueIdForJobsWithTheSameTitle(string jobId, string jobCode)
    {
        var handler = new JobDetailHandler { JobId = jobId, JobCode = jobCode };
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        using var cut = Render<JobDetail>(parameters => parameters.Add(component => component.JobId, jobId));

        cut.WaitForAssertion(() =>
        {
            cut.Find("h4").TextContent.Should().Be("Operator Light Equipment");
            cut.Find(".job-identifiers").TextContent.Should()
                .Contain("Job code:").And.Contain(jobCode).And.Contain("Job ID:");
            cut.Find(".job-identifiers code").TextContent.Should().Be(jobId);
        });
    }

    [Fact]
    public async Task SlowInitialStatusLoadCompletesBeforeTheFirstDelayAndUsesLiveSettings()
    {
        var handler = new JobDetailHandler
        {
            FirstSummaryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var clock = new ScoringThroughputPanelTests.ManualTimeProvider();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));
        Services.AddSingleton<TimeProvider>(clock);
        var cut = Render<CascadingValue<PollingSettingsState>>(parameters => parameters
            .Add(component => component.Value, new PollingSettingsState())
            .AddChildContent<JobDetail>(page => page.Add(component => component.JobId, "job-1")));
        cut.WaitForAssertion(() => handler.SummaryCalls.Should().Be(1));
        clock.ActiveTimers.Should().Be(0);

        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromMinutes(3)));
        handler.SummaryCalls.Should().Be(1);
        handler.FirstSummaryCompletion.SetResult();
        await WaitUntilAsync(() => clock.ActiveTimers == 1 && clock.TimeUntilNextTick == TimeSpan.FromSeconds(60));
        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(59)));
        handler.SummaryCalls.Should().Be(1);
        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(1)));
        cut.WaitForAssertion(() => handler.SummaryCalls.Should().Be(2));
        await WaitUntilAsync(() => clock.ActiveTimers == 1 && clock.TimeUntilNextTick == TimeSpan.FromSeconds(60));

        var settings = new PollingSettingsState();
        settings.Update(new(60, 120));
        cut.Render(parameters => parameters
            .Add(component => component.Value, settings)
            .AddChildContent<JobDetail>(page => page.Add(component => component.JobId, "job-1")));

        cut.FindComponent<JobDetail>().Instance.PollingSettings!.Current.StatusPollingIntervalSeconds.Should().Be(120);
        await WaitUntilAsync(() => clock.ActiveTimers == 1 && clock.TimeUntilNextTick == TimeSpan.FromSeconds(120));
        handler.SummaryCalls.Should().Be(2);
    }

    [Fact]
    public void StatusLoadFailureIsVisibleEvenBeforeAnyApplicationCountsAreAvailable()
    {
        var handler = new JobDetailHandler { FailSummary = true };
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        var cut = Render<JobDetail>(parameters => parameters.Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("Status refresh failed"));
    }

    [Fact]
    public async Task DisposingCancelsAnInFlightStatusPollAndStopsTheTimer()
    {
        var handler = new JobDetailHandler
        {
            FirstSummaryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockSummaryCall = 2,
        };
        var clock = new ScoringThroughputPanelTests.ManualTimeProvider();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));
        Services.AddSingleton<TimeProvider>(clock);
        var cut = Render<JobDetail>(parameters => parameters.Add(component => component.JobId, "job-1"));
        await WaitUntilAsync(() => clock.ActiveTimers == 1);
        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(60)));
        cut.WaitForAssertion(() => handler.SummaryCalls.Should().Be(2));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        handler.SummaryCancellationToken.IsCancellationRequested.Should().BeTrue();
        clock.ActiveTimers.Should().Be(0);
        clock.Advance(TimeSpan.FromMinutes(5));
        handler.SummaryCalls.Should().Be(2);
    }

    [Fact]
    public void JobDetail_LoadsSummaryAndShortlistFirstThenReviewOnDemand()
    {
        var handler = new JobDetailHandler();
        var api = new ApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        });
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        using var cut = Render<JobDetail>(parameters => parameters
            .Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() =>
        {
            handler.ApplicationQueries.Should().Contain("?list=shortlist");
            handler.ApplicationQueries.Should().NotContain("?list=longlist");
            handler.ApplicationQueries.Should().NotContain("?list=excluded");
            handler.ApplicationQueries.Should().NotContain("?list=review");
            cut.Find(".tab-row").TextContent.Should().Contain("Review (9)");
        });

        cut.FindAll(".tab-row button")
            .Single(button => button.TextContent.Contains("Review"))
            .Click();

        cut.WaitForAssertion(() =>
        {
            handler.ApplicationQueries.Should().Contain("?list=review");
            cut.FindAll("button").Should().Contain(button =>
                button.TextContent.Trim() == "Review");
        });

        cut.FindAll(".tab-row button")
            .Single(button => button.TextContent.Contains("Excluded"))
            .Click();

        cut.WaitForAssertion(() =>
        {
            handler.ApplicationQueries.Should().Contain("?list=excluded");
            cut.FindAll("button").Should().Contain(button =>
                button.TextContent.Trim() == "Review decision");
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }

    private sealed class JobDetailHandler : HttpMessageHandler
    {
        public string JobId { get; init; } = "job-1";
        public string JobCode { get; init; } = "JOB-1";
        public ConcurrentBag<string> ApplicationQueries { get; } = [];
        public TaskCompletionSource? FirstSummaryCompletion { get; init; }
        public int BlockSummaryCall { get; init; } = 1;
        public CancellationToken SummaryCancellationToken { get; private set; }
        public bool FailSummary { get; init; }
        private int summaryCalls;
        public int SummaryCalls => Volatile.Read(ref summaryCalls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/jobs/{JobId}/applications/summary")
            {
                var call = Interlocked.Increment(ref summaryCalls);
                SummaryCancellationToken = cancellationToken;
                if (FailSummary)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = JsonContent.Create(new { message = "Summary unavailable" }),
                    });
                if (call == BlockSummaryCall && FirstSummaryCompletion is not null)
                    return CompleteFirstSummaryAsync(cancellationToken);
                return Task.FromResult(SummaryResponse());
            }
            if (path == $"/api/jobs/{JobId}/applications")
            {
                ApplicationQueries.Add(request.RequestUri.Query);
                ApplicationDto[] applications = request.RequestUri.Query switch
                {
                    "?list=review" =>
                    [
                        new ApplicationDto(
                            "review-1",
                            JobId,
                            "candidate-review",
                            "Review Candidate",
                            null,
                            "NeedsManualReview",
                            75,
                            "NeedsManualReview",
                            18,
                            DateTime.UtcNow),
                    ],
                    "?list=excluded" =>
                    [
                        new ApplicationDto(
                            "excluded-1",
                            JobId,
                            "candidate-excluded",
                            "Excluded Candidate",
                            null,
                            "Completed",
                            60,
                            "Excluded",
                            2,
                            DateTime.UtcNow),
                    ],
                    _ => [],
                };
                return Task.FromResult(JsonResponse(applications));
            }

            var response = path switch
            {
                "/api/auth/me" => JsonResponse(new UserInfo(
                    "user-1",
                    "recruiter",
                    "recruiter",
                    "Engineering",
                    "Recruiter",
                    "recruiter@example.com")),
                var jobPath when jobPath == $"/api/jobs/{JobId}" => JsonResponse(new JobDto(
                    JobId,
                    JobCode,
                    "Operator Light Equipment",
                    "Engineering",
                    "TalentMatch",
                    DateTime.UtcNow,
                    "active",
                    null,
                    null,
                    "user-1",
                    DateTime.UtcNow)),
                var configPath when configPath == $"/api/jobs/{JobId}/config" => JsonResponse<JobConfigDto?>(null),
                var promptsPath when promptsPath == $"/api/jobs/{JobId}/prompts" => JsonResponse(Array.Empty<ScoringPromptDto>()),
                "/api/upload-sessions" => JsonResponse(Array.Empty<UploadSessionSummaryDto>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private async Task<HttpResponseMessage> CompleteFirstSummaryAsync(CancellationToken cancellationToken)
        {
            await FirstSummaryCompletion!.Task.WaitAsync(cancellationToken);
            return SummaryResponse();
        }

        private static HttpResponseMessage SummaryResponse() =>
            JsonResponse(new JobApplicationCountsDto(
                    Total: 299,
                    Pending: 0,
                    Uploading: 0,
                    Shortlist: 21,
                    Longlist: 87,
                    Excluded: 170,
                    Review: 9,
                    Queued: 0,
                    Scoring: 0,
                    Complete: 298,
                    Failed: 1));

        private static HttpResponseMessage JsonResponse<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
