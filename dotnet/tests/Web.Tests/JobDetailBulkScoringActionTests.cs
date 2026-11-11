using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

// The job page gains exactly one action, shown only when the environment has bulk scoring switched on;
// everything else on the page is unchanged.
public sealed class JobDetailBulkScoringActionTests : BunitContext
{
    private const string Label = "Start bulk scoring from SharePoint or OneDrive";

    [Fact]
    public void The_action_is_offered_next_to_upload_when_the_feature_is_on()
    {
        using var cut = RenderJobDetail(capabilities: Capabilities(enabled: true));

        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == Label));
        var upload = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload Applications");
        var bulk = cut.FindAll("button").Single(b => b.TextContent.Trim() == Label);
        bulk.HasAttribute("disabled").Should().Be(upload.HasAttribute("disabled"), "it has the same prerequisites as uploading");
        bulk.GetAttribute("title").Should().NotBeNullOrWhiteSpace();
        cut.FindAll("[role=dialog]").Should().BeEmpty("nothing opens until the action is used");
    }

    [Fact]
    public void The_action_is_hidden_when_the_feature_is_off()
    {
        using var cut = RenderJobDetail(capabilities: Capabilities(enabled: false));

        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Upload Applications"));
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("bulk scoring", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_page_is_unchanged_when_the_capability_check_fails()
    {
        using var cut = RenderJobDetail(capabilities: null);

        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Upload Applications"));
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("bulk scoring", StringComparison.OrdinalIgnoreCase));
        cut.Find("h4").TextContent.Should().Be("Platform Engineer");
        cut.WaitForAssertion(() => typeof(JobDetail)
            .GetField("statusRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance)
            .Should().BeOfType<AsyncRefreshLoop>("pipeline refresh must not depend on optional capability loading"));
    }

    [Fact]
    public void A_slow_capability_check_never_blocks_the_job_detail_shell()
    {
        var capabilityResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncRoutingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/bulk-scoring-requests/capabilities"
                ? capabilityResponse.Task
                : Task.FromResult(RouteBaseRequest(request)));
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        using var cut = Render<JobDetail>(parameters => parameters.Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() => cut.Find("h4").TextContent.Should().Be("Platform Engineer"));
        cut.FindAll("button").Should().NotContain(button => button.TextContent.Trim() == Label);

        cut.InvokeAsync(() => capabilityResponse.SetResult(Json(Capabilities(enabled: true))));
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(button => button.TextContent.Trim() == Label));
    }

    [Fact]
    public void Starting_a_new_bulk_run_clears_stale_retry_intent()
    {
        using var cut = RenderJobDetail(
            capabilities: Capabilities(enabled: true),
            initialUri: "http://localhost/jobs/job-1?retryBulkRequestId=33333333-3333-3333-3333-333333333333");

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Should().Contain(button => button.TextContent.Trim() == Label));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == Label).Click();

        cut.FindComponent<TalentMatch.Web.Client.Components.BulkScoringRequestDialog>()
            .Instance.RetryOfRequestId.Should().BeNull();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should()
            .Be("http://localhost/jobs/job-1");
    }

    [Fact]
    public void Product_sign_in_return_reopens_the_bulk_connection_dialog()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("sessionStorage.getItem", "talentMatch.bulkSource.resume.job-1")
            .SetResult("""{"FolderUrl":"https://contoso.sharepoint.com/Candidates"}""");
        using var cut = RenderJobDetail(
            capabilities: Capabilities(enabled: true),
            initialUri: "http://localhost/jobs/job-1?resumeBulkScoring=1");

        cut.WaitForAssertion(() => cut.Find("#bulk-folder-url").GetAttribute("value").Should()
            .Be("https://contoso.sharepoint.com/Candidates"));
    }

    [Fact]
    public void The_existing_upload_dialog_never_contacts_the_bulk_endpoints()
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        using var cut = Render<TalentMatch.Web.Client.Components.UploadApplications>(parameters => parameters.Add(c => c.JobId, "job-1"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Upload Applications"));
        cut.FindAll("input[type=checkbox]").Should().HaveCount(2, "the dialog keeps its two existing options");
        handler.Paths.Should().NotContain(p => p.Contains("bulk-scoring"));
    }

    private IRenderedComponent<JobDetail> RenderJobDetail(
        BulkCapabilitiesDto? capabilities,
        string? initialUri = null)
    {
        var httpClient = new HttpClient(new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me" => Json(new UserInfo("user-1", "admin", "admin", "IT", "Admin", "admin@example.com")),
            "/api/jobs/job-1" => Json(new JobDto(
                "job-1", "JOB-1", "Platform Engineer", "IT", "TalentMatch", DateTime.UtcNow, "active",
                null, null, "user-1", DateTime.UtcNow)),
            "/api/jobs/job-1/applications" => Json(Array.Empty<ApplicationDto>()),
            "/api/jobs/job-1/prompts" => Json(Array.Empty<ScoringPromptDto>()),
            "/api/jobs/job-1/config" => Json(new JobConfigDto("[]", "[]", "[]", 1, "average", 70, 85, 15)),
            "/api/bulk-scoring-requests/capabilities" when capabilities is not null => Json(capabilities),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));
        if (initialUri is not null)
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
                .NavigateTo(initialUri);
        return Render<JobDetail>(parameters => parameters.Add(component => component.JobId, "job-1"));
    }

    private static HttpResponseMessage RouteBaseRequest(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me" => Json(new UserInfo("user-1", "admin", "admin", "IT", "Admin", "admin@example.com")),
            "/api/jobs/job-1" => Json(new JobDto(
                "job-1", "JOB-1", "Platform Engineer", "IT", "TalentMatch", DateTime.UtcNow, "active",
                null, null, "user-1", DateTime.UtcNow)),
            "/api/jobs/job-1/applications" => Json(Array.Empty<ApplicationDto>()),
            "/api/jobs/job-1/prompts" => Json(Array.Empty<ScoringPromptDto>()),
            "/api/jobs/job-1/config" => Json(new JobConfigDto("[]", "[]", "[]", 1, "average", 70, 85, 15)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    private static BulkCapabilitiesDto Capabilities(bool enabled) =>
        new(enabled, enabled ? "mock" : "off", "same_tenant", ["sharepoint", "onedrive"], [], [".pdf", ".txt", ".docx", ".md"],
            new BulkLimitsDto(30_000, 8_388_608, 53_687_091_200, 30, 1_000));

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class AsyncRoutingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            route(request);
    }
}
