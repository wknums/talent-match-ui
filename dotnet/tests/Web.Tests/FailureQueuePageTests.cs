using System.Net;
using System.Net.Http.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class QueueHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
