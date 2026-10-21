using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class PollingSettingsProviderTests : BunitContext
{
    [Fact]
    public async Task OpenPagesReceiveConfigurationChangesWithoutReloading()
    {
        var handler = new SettingsHandler { Settings = new(90, 120) };
        var clock = Register(handler);
        var cut = Render<PollingSettingsProvider>(parameters => parameters.AddChildContent<Probe>());
        cut.WaitForAssertion(() => cut.Find("span").TextContent.Should().Be("90/120"));
        clock.TimeUntilNextTick.Should().Be(TimeSpan.FromSeconds(60));

        handler.Settings = new(180, 240);
        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(60)));

        cut.WaitForAssertion(() => cut.Find("span").TextContent.Should().Be("180/240"));
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ConfigurationFailureIsVisibleAndPreservesTheLastKnownSettings()
    {
        var handler = new SettingsHandler { Settings = new(90, 120) };
        var clock = Register(handler);
        var cut = Render<PollingSettingsProvider>(parameters => parameters.AddChildContent<Probe>());
        cut.WaitForAssertion(() => cut.Find("span").TextContent.Should().Be("90/120"));
        handler.Fail = true;

        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(60)));

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("last known intervals"));
        cut.Find("span").TextContent.Should().Be("90/120");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"throughputRefreshIntervalSeconds":0,"statusPollingIntervalSeconds":60}""")]
    [InlineData("""{"throughputRefreshIntervalSeconds":60,"statusPollingIntervalSeconds":4294968}""")]
    public async Task ApiRejectsMissingOrUnsupportedIntervals(string body)
    {
        using var http = new HttpClient(new InvalidSettingsHandler(body)) { BaseAddress = new Uri("http://localhost") };
        var action = () => new ApiClient(http).GetPollingSettingsAsync();

        await action.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public void SignedOutRoutesUseDefaultsWithoutAConfigurationFailureBanner()
    {
        var handler = new SettingsHandler { SignedOut = true };
        Register(handler);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/login");

        var cut = Render<PollingSettingsProvider>(parameters => parameters.AddChildContent<Probe>());

        cut.WaitForAssertion(() => cut.Find("span").TextContent.Should().Be("60/60"));
        handler.Calls.Should().Be(0);
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/login?returnUrl=%2Fjobs")]
    [InlineData("/authentication/login")]
    [InlineData("/authentication/login-callback?code=test-code")]
    [InlineData("/authentication/logout")]
    public async Task AuthenticationRoutesDoNotRequestProtectedSettingsAndResumeAfterSignIn(string route)
    {
        var handler = new SettingsHandler { Settings = new(90, 120) };
        var clock = Register(handler);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(route);
        var cut = Render<PollingSettingsProvider>(parameters => parameters.AddChildContent<Probe>());

        await cut.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(60)));

        handler.Calls.Should().Be(0);
        cut.Find("span").TextContent.Should().Be("60/60");
        cut.FindAll("[role=alert]").Should().BeEmpty();

        await cut.InvokeAsync(() => navigation.NavigateTo("/jobs/job-1"));

        cut.WaitForAssertion(() => cut.Find("span").TextContent.Should().Be("90/120"));
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public void InteractiveTokenFailureOffersSignInWithoutDisplayingTheScope()
    {
        var handler = new SettingsHandler();
        Register(handler);
        var navigation = Services.GetRequiredService<NavigationManager>();
        handler.Failure = new AccessTokenNotAvailableException(navigation,
            new AccessTokenResult(
                AccessTokenResultStatus.RequiresRedirect, new AccessToken(),
                "authentication/login", new InteractiveRequestOptions
                {
                    Interaction = InteractionType.GetToken, ReturnUrl = navigation.Uri,
                }),
            ["api://test/access_as_user"]);

        var cut = Render<PollingSettingsProvider>(parameters => parameters.AddChildContent<Probe>());

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=alert]").TextContent.Should().Contain("Sign in again");
            cut.Markup.Should().NotContain("api://");
            cut.Find("[data-testid=polling-sign-in]").Should().NotBeNull();
        });
    }

    private ScoringThroughputPanelTests.ManualTimeProvider Register(SettingsHandler handler)
    {
        var clock = new ScoringThroughputPanelTests.ManualTimeProvider();
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddSingleton(PublicAuthConfiguration.Simple("http://localhost/"));
        Services.AddSingleton(new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return clock;
    }

    public sealed class Probe : ComponentBase
    {
        [CascadingParameter] public PollingSettingsState Settings { get; set; } = new();
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddContent(1, $"{Settings.Current.ThroughputRefreshIntervalSeconds}/{Settings.Current.StatusPollingIntervalSeconds}");
            builder.CloseElement();
        }
    }

    private sealed class SettingsHandler : HttpMessageHandler
    {
        public PollingSettingsDto Settings { get; set; } = new();
        public bool Fail { get; set; }
        public bool SignedOut { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/system-configuration/polling");
            Calls++;
            if (Failure is not null)
                throw Failure;
            if (SignedOut)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = Fail ? new StringContent("""{"message":"Configuration unavailable"}""") : JsonContent.Create(Settings),
            });
        }
    }

    private sealed class InvalidSettingsHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
