using System.Net;
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
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Failed to load statistics"));
    }
}