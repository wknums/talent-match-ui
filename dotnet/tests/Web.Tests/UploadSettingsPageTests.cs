using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class UploadSettingsPageTests : BunitContext
{
    [Fact]
    public void Page_RendersBinaryDefaultsAndNoRunnerOrScoringControl()
    {
        var handler = new SettingsHandler();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        Services.AddSingleton(api);

        var cut = Render<UploadSettings>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("h3#upload-settings-title").TextContent.Should().Be("Optional upload settings");
            cut.FindAll("h1").Should().BeEmpty();
            cut.FindAll(".upload-settings-field").Should().HaveCount(3);
            cut.Markup.Should().Contain("4");
            cut.Markup.Should().Contain("4194304");
            cut.Markup.Should().Contain("104857600");
            cut.Markup.ToLowerInvariant().Should().NotContain("runner");
            cut.Markup.ToLowerInvariant().Should().NotContain("scoring concurrency");
        });
    }

    private sealed class SettingsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new UploadSettingsDto(
                    4, 4_194_304, 104_857_600, 0, false, null, null)),
            });
    }
}
