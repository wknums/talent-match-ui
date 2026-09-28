using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class JobDetailLazyLoadingTests : BunitContext
{
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

    private sealed class JobDetailHandler : HttpMessageHandler
    {
        public ConcurrentBag<string> ApplicationQueries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/jobs/job-1/applications/summary")
            {
                return Task.FromResult(JsonResponse(new JobApplicationCountsDto(
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
                    Failed: 1)));
            }
            if (path == "/api/jobs/job-1/applications")
            {
                ApplicationQueries.Add(request.RequestUri.Query);
                ApplicationDto[] applications = request.RequestUri.Query switch
                {
                    "?list=review" =>
                    [
                        new ApplicationDto(
                            "review-1",
                            "job-1",
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
                            "job-1",
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
                "/api/jobs/job-1" => JsonResponse(new JobDto(
                    "job-1",
                    "JOB-1",
                    "Operator Light Equipment",
                    "Engineering",
                    "TalentMatch",
                    DateTime.UtcNow,
                    "active",
                    null,
                    null,
                    "user-1",
                    DateTime.UtcNow)),
                "/api/jobs/job-1/config" => JsonResponse<JobConfigDto?>(null),
                "/api/jobs/job-1/prompts" => JsonResponse(Array.Empty<ScoringPromptDto>()),
                "/api/upload-sessions" => JsonResponse(Array.Empty<UploadSessionSummaryDto>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage JsonResponse<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
