using System.Net;
using System.Net.Http.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class FailureQueuePageTests : BunitContext
{
    [Fact]
    public void Page_RendersPersistedFailureQueueRows()
    {
        var item = new DlqItemDto(
            "failure-1",
            "Application",
            "application-1",
            "Scoring failed while connecting to storage.",
            0,
            DateTime.UtcNow);
        var api = new ApiClient(new HttpClient(new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { item }),
            }))
        {
            BaseAddress = new Uri("http://localhost"),
        });
        Services.AddSingleton(api);

        using var cut = Render<FailureQueue>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("application-1");
            cut.Markup.Should().Contain("Scoring failed while connecting to storage.");
            cut.Markup.Should().NotContain("No failed items in the queue.");
        });
    }

    [Fact]
    public void Page_ShowsLoadFailureInsteadOfFalseEmptyState()
    {
        var api = new ApiClient(new HttpClient(new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        {
            BaseAddress = new Uri("http://localhost"),
        });
        Services.AddSingleton(api);

        using var cut = Render<FailureQueue>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role='alert']").TextContent.Should()
                .Contain("Failed to refresh the failure queue.");
            cut.Markup.Should().NotContain("No failed items in the queue.");
        });
    }

    [Fact]
    public void Page_projects_bulk_failures_and_reconnects_before_retry()
    {
        var api = new ApiClient(new HttpClient(new BulkQueueHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        });
        Services.AddSingleton(api);

        using var cut = Render<FailureQueue>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid=bulk-failure-row]").Should().ContainSingle();
            cut.Find("[data-testid=bulk-failure-queue]").TextContent.Should()
                .Contain("1 failed")
                .And.Contain("Reconnect & retry");
        });

        cut.FindAll("button").Single(button => button.TextContent.Contains("Reconnect")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should()
            .EndWith("/jobs/job-1?retryBulkRequestId=request-1");
    }

    private sealed class QueueHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath == "/api/dlq"
                    ? response
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(Array.Empty<BulkTerminalNotificationDto>()),
                    });
    }

    private sealed class BulkQueueHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/dlq")
                return Task.FromResult(Json(Array.Empty<DlqItemDto>()));
            if (path == "/api/bulk-scoring-requests/notifications")
                return Task.FromResult(Json(new[]
                {
                    new BulkTerminalNotificationDto(
                        "request-1",
                        "actor-1",
                        "partial",
                        new BulkTerminalCountsDto(67, 1, 0, 0),
                        DateTime.UtcNow,
                        "/api/bulk-scoring-requests/request-1"),
                }));
            if (path == "/api/bulk-scoring-requests/request-1")
                return Task.FromResult(Json(new BulkRequestDetailDto(
                    "request-1",
                    "job-1",
                    "completed",
                    "partial",
                    "sharepoint",
                    "Candidates",
                    "/Candidates",
                    "https://contoso.sharepoint.com/Candidates",
                    "Recruiter",
                    DateTime.UtcNow.AddMinutes(-10),
                    DateTime.UtcNow,
                    true,
                    false,
                    false,
                    null,
                    false,
                    new BulkCountsDto(68, 0, 0, 0, 0, 0, 0, 67, 1, 0, 0),
                    2,
                    new BulkLimitsDto(30_000, 8_388_608, 53_687_091_200, 30, 1_000),
                    "config-1",
                    false,
                    null,
                    "delegated",
                    "active",
                    "tenant-1")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
