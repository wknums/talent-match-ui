using System.Net;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public class DashboardTests : BunitContext
{
    [Fact]
    public void UnauthorizedStatsResponse_ShowsErrorWithoutRenderingFailure()
    {
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/auth/me", HttpStatusCode.Unauthorized, "{}");
        handler.SetupResponse("GET", "/api/jobs", HttpStatusCode.OK, "[]");
        handler.SetupResponse("GET", "/api/stats", HttpStatusCode.Unauthorized, "{}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Failed to load statistics"));
    }

    [Fact]
    public void Dashboard_ShowsActiveUploadsBetweenApplicationsAndQueued()
    {
        var now = DateTime.UtcNow;
        var detail = new UploadSessionDetailDto(
            "session-1",
            "job-1",
            "active",
            false,
            new(4, 4_194_304, 104_857_600),
            new(5, 2, 1, 1, 0, 1, 0, 2),
            40,
            "correlation-1",
            now,
            now,
            now,
            null,
            2,
            []);
        var summary = new UploadSessionSummaryDto(
            detail.Id,
            detail.JobId,
            detail.Status,
            detail.AllowDuplicates,
            detail.Limits,
            detail.Counts,
            detail.ProgressPercent,
            detail.CorrelationId,
            detail.CreatedAt,
            detail.StartedAt,
            detail.LastHeartbeatAt,
            detail.CompletedAt,
            detail.ConcurrencyVersion);
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/auth/me", HttpStatusCode.OK, """
            { "id":"recruiter-1", "username":"recruiter", "role":"recruiter", "department":"IT", "fullName":"Recruiter", "email":"recruiter@example.com" }
            """);
        handler.SetupResponse("GET", "/api/jobs", HttpStatusCode.OK, "[]");
        handler.SetupResponse("GET", "/api/stats", HttpStatusCode.OK, """
            { "queued":2, "extracting":0, "scoring":0, "aggregating":0, "completed":7, "needsManualReview":0, "failed":1, "totalJobs":3, "totalApplications":10 }
            """);
        handler.SetupResponse(
            "GET",
            "/api/upload-sessions",
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new[] { summary }));
        handler.SetupResponse(
            "GET",
            "/api/upload-sessions/session-1",
            HttpStatusCode.OK,
            JsonSerializer.Serialize(detail));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            var tiles = cut.FindAll("div[style*='min-width: 120px']");
            tiles.Select(tile => string.Concat(tile.TextContent.Where(character => !char.IsWhiteSpace(character))))
                .Should().ContainInOrder(
                "3TotalJobs",
                "10Applications",
                "3ActiveUploads",
                "2Queued");
            cut.FindAll("#upload-activity").Should().BeEmpty();
        });
    }
}