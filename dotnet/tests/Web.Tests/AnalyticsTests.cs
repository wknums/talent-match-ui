using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class AnalyticsTests : BunitContext
{
    [Fact]
    public void RecruiterAnalyticsFailure_IsVisibleInsteadOfLookingLikeZeroData()
    {
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/stats/recruiters", HttpStatusCode.Forbidden,
            """{"error":"forbidden","message":"Recruiter analytics are not available for this account."}""");
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(client));

        var cut = Render<Analytics>();

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent
            .Should().Contain("Recruiter analytics are not available for this account."));
    }
}