using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class JobUsabilityComponentsTests : BunitContext
{
    [Fact]
    public void JobDetail_ShowsPersistedBackgroundScoringFailuresWithoutAProcessResponse()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me" => JsonResponse(new UserInfo("user-1", "admin", "admin", "IT", "Admin", "admin@example.com")),
            "/api/jobs/job-1" => JsonResponse(new JobDto(
                "job-1", "JOB-1", "Test Job", "IT", "TalentMatch", DateTime.UtcNow, "active",
                null, null, "user-1", DateTime.UtcNow)),
            "/api/jobs/job-1/applications" => JsonResponse(new[]
            {
                new ApplicationDto("app-1", "job-1", "candidate-1", "Candidate One", null,
                    "ScoringFailed", null, null, null, DateTime.UtcNow, "Scoring interrupted; retry explicitly."),
            }),
            "/api/jobs/job-1/prompts" => JsonResponse(Array.Empty<ScoringPromptDto>()),
            "/api/jobs/job-1/config" => JsonResponse<JobConfigDto?>(null),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<JobDetail>(parameters => parameters.Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Failed (1)");
            cut.Markup.Should().Contain("Scoring interrupted; retry explicitly.");
            cut.Markup.Should().Contain("Processing Errors (1)");
        });
    }

    [Fact]
    public void UploadApplications_ShowsDefaultOffDuplicateOverride()
    {
        using var httpClient = new HttpClient(new EmptyHandler()) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<UploadApplications>(parameters => parameters
            .Add(component => component.JobId, "job-1"));

        var checkbox = cut.Find("input[type='checkbox']");
        checkbox.HasAttribute("checked").Should().BeFalse();
        cut.Markup.Should().Contain("Allow duplicate documents");
    }

    [Fact]
    public void FloatingPanel_CanBeMovedResizedAndClosed()
    {
        var closeCount = 0;
        var cut = Render<FloatingPanel>(parameters => parameters
            .Add(component => component.Title, "Prompt Management")
            .Add(component => component.ChildContent, builder => builder.AddContent(0, "Prompt content"))
            .Add(component => component.OnClose, EventCallback.Factory.Create(this, () => closeCount++)));

        cut.Find(".floating-panel-header").MouseDown(new MouseEventArgs { ClientX = 10, ClientY = 10 });
        cut.Find(".floating-panel-backdrop").MouseMove(new MouseEventArgs { ClientX = 50, ClientY = 70 });

        cut.Find(".floating-panel").GetAttribute("style").Should().Contain("left: 104px").And.Contain("top: 108px");
        cut.Markup.Should().Contain("resize: both");

        cut.Find(".floating-panel-close").Click();
        closeCount.Should().Be(1);
    }

    [Fact]
    public void CreateJobDialog_SectionsCanCollapseAndMove()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<int>("eval").SetResult(1200);
        using var httpClient = new HttpClient(new EmptyHandler()) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));
        Services.AddSingleton(PublicAuthConfiguration.Simple("http://localhost/"));

        var cut = Render<CreateJobDialog>(parameters => parameters
            .Add(component => component.Visible, true));

        var rubricSection = FindSection(cut, "Rubric and requirements");
        rubricSection.GetAttribute("style").Should().Contain("order: 1");
        cut.Markup.Should().Contain("Rubric Categories");
        var rubricToggle = rubricSection.QuerySelector(".editor-section-toggle")!;
        rubricToggle.TextContent.Trim().Should().Be("Rubric and requirements");
        rubricToggle.QuerySelector(".disclosure-chevron").Should().NotBeNull();
        rubricToggle.GetAttribute("aria-label").Should().Be("Collapse Rubric and requirements");

        rubricToggle.Click();
        cut.Markup.Should().NotContain("Rubric Categories");

        rubricSection = FindSection(cut, "Rubric and requirements");
        rubricSection.QuerySelector(".editor-section-toggle")!
            .GetAttribute("aria-label").Should().Be("Expand Rubric and requirements");
        rubricSection.QuerySelector(".editor-section-toggle")!.Click();
        rubricSection = FindSection(cut, "Rubric and requirements");
        rubricSection.QuerySelectorAll(".editor-section-move-controls button")[1].Click();

        FindSection(cut, "Rubric and requirements")
            .GetAttribute("style").Should().Contain("order: 2");
    }

    [Fact]
    public void PromptManagement_LabelsTheCurrentProductionPrompt()
    {
        var prompts = new[]
        {
            new ScoringPromptDto("prompt-2", "job-1", 2, "Second", "active", DateTime.UtcNow, DateTime.UtcNow, "admin", null, null, "generated", null),
            new ScoringPromptDto("prompt-1", "job-1", 1, "First", "inactive", DateTime.UtcNow, DateTime.UtcNow, "admin", null, null, "generated", null),
        };
        using var httpClient = new HttpClient(new RoutingHandler(request =>
            JsonResponse(prompts))) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<PromptManagement>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.HasApprovedRubric, true)
            .Add(component => component.ProductionPromptId, "prompt-2"));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tbody tr");
            rows.Should().HaveCount(2);
            rows[0].TextContent.Should().Contain("Current production prompt");
            rows[0].TextContent.Should().NotContain("Use for Production");
            rows[1].TextContent.Should().Contain("Use for Production");
        });
    }

    [Fact]
    public async Task JobDetail_PrioritizesPipelineAndAutomaticallyProcessesUploadedApplications()
    {
        var processRequests = 0;
        var rubric = new RubricEnvelopeDto(
            "rubric-v2",
            null,
            [new RubricCategoryV2Dto("cat-1", "Technical", 1, null, 0)],
            []);
        var extraction = new ExtractionSummaryDto(
            "extraction-1",
            "instruction-1",
            "extraction-rubric-v1",
            "valid",
            [],
            "job-spec.pdf",
            "application/pdf",
            DateTime.UtcNow,
            "correlation-1");
        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/jobs/job-1/process")
            {
                processRequests++;
                return JsonResponse(new ProcessJobResponse(0, 1, ["Application app-1: transient scoring error"]));
            }

            return path switch
            {
                "/api/auth/me" => JsonResponse(new UserInfo("user-1", "admin", "admin", "Engineering", "Admin", "admin@example.com")),
                "/api/jobs/job-1" => JsonResponse(new JobDto("job-1", "JOB-1", "Test Job", "Engineering", "TalentMatch", DateTime.UtcNow, "active", "config-1", "Description", "user-1", DateTime.UtcNow)),
                "/api/jobs/job-1/applications" => processRequests == 0
                    ? JsonResponse(Array.Empty<ApplicationDto>())
                    : JsonResponse(new[]
                    {
                        new ApplicationDto("app-1", "job-1", "candidate-1", "Candidate One", null, "Completed", 82, "Eligible", 2, DateTime.UtcNow),
                    }),
                "/api/jobs/job-1/config" => JsonResponse(new JobConfigDto(
                    JsonSerializer.Serialize(rubric),
                    "[]",
                    "[]",
                    3,
                    "median",
                    70,
                    85,
                    15,
                    "approved",
                    "generated",
                    extraction.Id,
                    extraction.InstructionVersionId,
                    "config-1",
                    1,
                    extraction)),
                "/api/jobs/job-1/prompts" => JsonResponse(new[]
                {
                    new ScoringPromptDto("prompt-2", "job-1", 2, "Production prompt", "production-approved", DateTime.UtcNow, DateTime.UtcNow, "admin", null, null, "generated", null),
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<JobDetail>(parameters => parameters
            .Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() => cut.Find(".pipeline-section"));
        cut.FindAll(".action-row button").Single(button => button.TextContent.Trim() == "Help").Click();

        cut.WaitForAssertion(() =>
        {
            var sections = cut.FindAll(".pipeline-section, .job-help, .job-collapsible");
            sections.First().ClassList.Should().Contain("pipeline-section");
            sections.Last().TextContent.Should().Contain("Extraction diagnostics");
        });

        cut.FindAll(".action-row button").Single(button => button.TextContent.Trim() == "Upload Applications").Click();
        var upload = cut.FindComponent<UploadApplications>();
        await cut.InvokeAsync(() => upload.Instance.OnUploaded.InvokeAsync());

        processRequests.Should().Be(1);
        cut.Markup.Should().Contain("Complete (1)");
        cut.Markup.Should().NotContain("Processing Errors");
    }

    [Fact]
    public void ManualReview_DisplaysPascalCasedRubricV2Categories()
    {
        var rubric = new RubricEnvelopeDto(
            "rubric-v2",
            null,
            [new RubricCategoryV2Dto("cat-1", "Technical Experience", 1, "Assess relevant experience.", 0)],
            []);
        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/auth/me" => JsonResponse(new UserInfo("user-1", "reviewer", "reviewer", "Engineering", "Reviewer", "reviewer@example.com")),
                "/api/applications/app-1" => JsonResponse(new ApplicationDto("app-1", "job-1", "candidate-1", "Candidate One", null, "NeedsManualReview", 75, "NeedsManualReview", 4, DateTime.UtcNow)),
                "/api/applications/app-1/manual-review" => JsonResponse(new ManualReviewDto("{}", "", null, "[]", true, "NeedsManualReview")),
                "/api/applications/app-1/documents" => JsonResponse(Array.Empty<DocumentDto>()),
                "/api/jobs/job-1/config" => JsonResponse(new JobConfigDto(
                    JsonSerializer.Serialize(rubric),
                    "[]",
                    "[]",
                    3,
                    "median",
                    70,
                    85,
                    15)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ManualReview>(parameters => parameters
            .Add(component => component.ApplicationId, "app-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Technical Experience");
            cut.Markup.Should().NotContain("No rubric configuration available for this job.");
        });
    }

    [Fact]
    public void ManualReview_ContinuesToDisplayLegacyRubricCategories()
    {
        var legacyRubric = new[]
        {
            new RubricCategoryItem("Legacy Experience", 1, "Assess the original legacy requirements."),
        };
        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/auth/me" => JsonResponse(new UserInfo("user-1", "reviewer", "reviewer", "Engineering", "Reviewer", "reviewer@example.com")),
                "/api/applications/app-1" => JsonResponse(new ApplicationDto("app-1", "job-1", "candidate-1", "Candidate One", null, "NeedsManualReview", 75, "NeedsManualReview", 4, DateTime.UtcNow)),
                "/api/applications/app-1/manual-review" => JsonResponse(new ManualReviewDto("{}", "", null, "[]", true, "NeedsManualReview")),
                "/api/applications/app-1/documents" => JsonResponse(Array.Empty<DocumentDto>()),
                "/api/jobs/job-1/config" => JsonResponse(new JobConfigDto(
                    JsonSerializer.Serialize(legacyRubric),
                    """["Original mandatory requirement"]""",
                    "[]",
                    3,
                    "median",
                    70,
                    85,
                    15)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ManualReview>(parameters => parameters
            .Add(component => component.ApplicationId, "app-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Legacy Experience");
            cut.Markup.Should().Contain("Original mandatory requirement");
            cut.Markup.Should().NotContain("No rubric configuration available for this job.");
        });
    }

    private static AngleSharp.Dom.IElement FindSection(IRenderedComponent<CreateJobDialog> cut, string text)
        => cut.FindAll(".editor-section").Single(section => section.TextContent.Contains(text, StringComparison.Ordinal));

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
}
