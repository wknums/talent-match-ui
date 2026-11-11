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
    public void ReasoningProfileFieldsRenderAllBackendSupportedEfforts()
    {
        var catalog = new ReasoningModelsResponseDto(
            "reasoning-model",
            "very high",
            ["low", "very high", "maximum"],
            [new ReasoningModelOptionDto("reason01", "reasoning-model", true)]);

        using var cut = Render<ReasoningProfileFields>(parameters => parameters
            .Add(component => component.Catalog, catalog)
            .Add(component => component.ModelId, "reasoning-model")
            .Add(component => component.ReasoningLevel, "very high"));

        var options = cut.FindAll("label")
            .Single(label => label.TextContent.Contains("Reasoning effort"))
            .QuerySelectorAll("option")
            .Select(option => option.TextContent.Trim());

        options.Should().Equal("low", "very high", "maximum");
    }

    [Fact]
    public void JobDetail_OffersCsvAndXlsxExportsForEachCandidateList()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me" => JsonResponse(new UserInfo("user-1", "admin", "admin", "IT", "Admin", "admin@example.com")),
            "/api/jobs/job-1" => JsonResponse(new JobDto(
                "job-1", "JOB-1", "Platform Engineer", "IT", "TalentMatch", DateTime.UtcNow, "active",
                null, null, "user-1", DateTime.UtcNow)),
            "/api/jobs/job-1/applications" => JsonResponse(new[]
            {
                new ApplicationDto("app-1", "job-1", "candidate-1", "Candidate One", "candidate@example.test",
                    "Completed", 90, "Eligible", 2, DateTime.UtcNow),
                new ApplicationDto("app-2", "job-1", "candidate-2", "Candidate Two", null,
                    "Completed", 40, "Excluded", 3, DateTime.UtcNow),
            }),
            "/api/jobs/job-1/prompts" => JsonResponse(Array.Empty<ScoringPromptDto>()),
            "/api/jobs/job-1/config" => JsonResponse(new JobConfigDto(
                "[]", "[]", "[]", 1, "average", 70, 85, 15)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        using var cut = Render<JobDetail>(parameters => parameters.Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() =>
            cut.Find("button[aria-label='Export shortlist applications']").Should().NotBeNull());

        cut.Find("button[aria-label='Export shortlist applications']").Click();
        cut.FindAll("[role='menuitem']").Select(item => item.TextContent).Should()
            .BeEquivalentTo(["Export as CSV", "Export as XLSX"]);

        cut.FindAll(".tab-row button")[1].Click();
        cut.Find("button[aria-label='Export longlist applications']").Should().NotBeNull();

        cut.FindAll(".tab-row button")[2].Click();
        cut.Find("button[aria-label='Export review applications']").Should().NotBeNull();

        cut.FindAll(".tab-row button")[3].Click();
        cut.Find("button[aria-label='Export excluded applications']").Should().NotBeNull();
    }

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
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        using var cut = Render<JobDetail>(parameters => parameters.Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Failed (1)");
            cut.Markup.Should().Contain("Scoring interrupted; retry explicitly.");
            cut.Markup.Should().Contain("Processing Errors (1)");
        });
    }

    [Fact]
    public void UploadApplications_ShowsBothOptionsDefaultOff()
    {
        using var httpClient = new HttpClient(new EmptyHandler()) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        var cut = Render<UploadApplications>(parameters => parameters
            .Add(component => component.JobId, "job-1"));

        var checkboxes = cut.FindAll("input[type='checkbox']");
        checkboxes.Should().HaveCount(2);
        checkboxes.Should().OnlyContain(checkbox => !checkbox.HasAttribute("checked"));
        cut.Markup.Should().Contain("Allow duplicate documents");
        cut.Markup.Should().Contain("Upload files individually in the background");
    }

    [Fact]
    public void UploadApplications_OptionalOffCallsOnlyLegacyMultipartEndpoint()
    {
        var legacyCalls = 0;
        var optionalCalls = 0;
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/jobs/job-1/applications/upload")
            {
                legacyCalls++;
                request.RequestUri.Query.Should().Contain("allowDuplicates=false");
                return JsonResponse(new[] { new { id = "app-1" } });
            }
            optionalCalls++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));
        var cut = Render<UploadApplications>(parameters => parameters
            .Add(component => component.JobId, "job-1"));
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
            InputFileContent.CreateFromText("cv", "candidate.txt", contentType: "text/plain"));

        cut.FindAll("button").Single(button => button.TextContent.Trim().StartsWith("Upload ")).Click();

        cut.WaitForAssertion(() => legacyCalls.Should().Be(1));
        optionalCalls.Should().Be(0);
    }

    [Fact]
    public void UploadApplications_OptionalModeAllowsConfiguredLimitAboveLegacyFifteenMb()
    {
        const int fileSize = 15 * 1024 * 1024 + 1;
        var createCalls = 0;
        var contentCalls = 0;
        UploadItemDto? current = null;
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/jobs/job-1/upload-sessions")
            {
                createCalls++;
                var create = JsonSerializer.Deserialize<CreateUploadSessionRequest>(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                current = new UploadItemDto(
                    "item-1",
                    "session-1",
                    create.Items.Single().OccurrenceKey,
                    0,
                    "candidate.pdf",
                    "application/pdf",
                    fileSize,
                    "waiting",
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    null,
                    1);
                return JsonResponse(new UploadSessionDetailDto(
                    "session-1",
                    "job-1",
                    "active",
                    false,
                    new(1, 20 * 1024 * 1024, 20 * 1024 * 1024),
                    new(1, 1, 0, 0, 0, 0, 0, 0),
                    0,
                    "correlation-1",
                    DateTime.UtcNow,
                    null,
                    DateTime.UtcNow,
                    null,
                    1,
                    [current]));
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/item-1/content"))
            {
                contentCalls++;
                current = current! with
                {
                    Status = "succeeded",
                    CompletedAt = DateTime.UtcNow,
                    ConcurrencyVersion = 2,
                };
                return JsonResponse(current);
            }
            if (request.Method == HttpMethod.Get && path == "/api/upload-sessions/session-1")
                return JsonResponse(new UploadSessionDetailDto(
                    "session-1",
                    "job-1",
                    "completed",
                    false,
                    new(1, 20 * 1024 * 1024, 20 * 1024 * 1024),
                    new(1, 0, 0, 1, 0, 0, 0, 1),
                    100,
                    "correlation-1",
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    2,
                    [current!]));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));
        var cut = Render<UploadApplications>(parameters => parameters
            .Add(component => component.JobId, "job-1"));
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
            InputFileContent.CreateFromText(
                new string('x', fileSize),
                "candidate.pdf",
                contentType: "application/pdf"));

        cut.FindAll("button").Single(button => button.TextContent.Trim().StartsWith("Upload "))
            .HasAttribute("disabled").Should().BeTrue("legacy mode keeps the exact 15 MB limit");
        cut.FindAll("input[type='checkbox']")[1].Change(true);
        cut.FindAll("button").Single(button => button.TextContent.Trim().StartsWith("Upload ")).Click();

        cut.WaitForAssertion(() =>
        {
            createCalls.Should().Be(1);
            contentCalls.Should().Be(1);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UploadApplications_OptionalSubmitGuardPreventsDuplicateSessionsAndClosesOnce()
    {
        var createCalls = 0;
        var closed = 0;
        Guid occurrenceKey = default;
        var createResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new AsyncRoutingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/api/jobs/job-1/upload-sessions")
            {
                createCalls++;
                var create = JsonSerializer.Deserialize<CreateUploadSessionRequest>(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                occurrenceKey = create.Items.Single().OccurrenceKey;
                return createResponse.Task;
            }
            return Task.FromResult(JsonResponse(new UploadSessionDetailDto(
                "session-1",
                "job-1",
                "completed",
                false,
                new(4, 4_194_304, 104_857_600),
                new(0, 0, 0, 0, 0, 0, 0, 0),
                0,
                "correlation-1",
                DateTime.UtcNow,
                null,
                DateTime.UtcNow,
                DateTime.UtcNow,
                1,
                [])));
        }))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));
        var cut = Render<UploadApplications>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.OnOptionalUploadStarted, () => closed++));
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
            InputFileContent.CreateFromText("cv", "candidate.txt", contentType: "text/plain"));
        var start = typeof(UploadApplications).GetMethod(
            "StartOptionalUpload",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var first = (Task)start.Invoke(cut.Instance, null)!;
        var second = (Task)start.Invoke(cut.Instance, null)!;

        createCalls.Should().Be(1);
        createResponse.SetResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new UploadSessionDetailDto(
                "session-1",
                "job-1",
                "active",
                false,
                new(4, 4_194_304, 104_857_600),
                new(1, 1, 0, 0, 0, 0, 0, 0),
                0,
                "correlation-1",
                DateTime.UtcNow,
                null,
                DateTime.UtcNow,
                null,
                1,
                [
                    new UploadItemDto(
                        "item-1",
                        "session-1",
                        occurrenceKey,
                        0,
                        "candidate.txt",
                        "text/plain",
                        2,
                        "waiting",
                        0,
                        null,
                        null,
                        null,
                        null,
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        null,
                        1),
                ])),
        });
        await Task.WhenAll(first, second);

        createCalls.Should().Be(1);
        closed.Should().Be(1);
        var entries = typeof(UploadApplications).GetField(
            "fileEntries",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cut.Instance) as System.Collections.ICollection;
        entries.Should().NotBeNull();
        entries!.Count.Should().Be(0);
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
    public void PromptManagementLoadsOnlyTheViewedPromptHistoryOnDemand()
    {
        var testHistoryRequests = new List<string>();
        var prompts = new[]
        {
            new ScoringPromptDto("prompt-2", "job-1", 2, "Active prompt text", "active", DateTime.UtcNow, DateTime.UtcNow, "admin", null, null, "generated", null, null, "o3", "high"),
            new ScoringPromptDto("prompt-1", "job-1", 1, "Historical prompt text", "inactive", DateTime.UtcNow, DateTime.UtcNow, "admin", null, null, "manual", null, null, "o3", "medium"),
        };
        var historicalRun = new PromptTestRunDto(
            "historical-run-1",
            "job-1",
            "prompt-1",
            "approved",
            "[]",
            DateTime.UtcNow,
            DateTime.UtcNow,
            "reviewer",
            null,
            "o3",
            "medium");
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/test-runs", StringComparison.Ordinal))
            {
                testHistoryRequests.Add(path);
                return path.Contains("prompt-1", StringComparison.Ordinal)
                    ? JsonResponse(new[] { historicalRun })
                    : JsonResponse(Array.Empty<PromptTestRunDto>());
            }

            return path switch
            {
                "/api/jobs/job-1/prompts" => JsonResponse(prompts),
                "/api/reasoning-models" => JsonResponse(ReasoningCatalog()),
                "/api/jobs/job-1/prompt-generation-instructions" =>
                    JsonResponse(Array.Empty<PromptGenerationInstructionDto>()),
                "/api/jobs/job-1/config" => JsonResponse<JobConfigDto?>(null),
                "/api/jobs/job-1/prompts/prompt-1/profile" =>
                    JsonResponse(new PromptProfileStatusDto(
                        "prompt-1", "o3", "medium", "o3", "medium",
                        false, false, null, "Testing required")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<PromptManagement>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.HasApprovedRubric, true));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Should().HaveCount(2);
            cut.FindComponents<PromptTestRunner>().Should().BeEmpty();
            testHistoryRequests.Should().BeEmpty();
        });

        cut.Find("button[aria-label='View prompt v1 test details and history']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<PromptTestRunner>();
            cut.Markup.Should().Contain("Historical prompt text");
            cut.Markup.Should().Contain("historic...");
            testHistoryRequests.Should().Equal(
                "/api/jobs/job-1/prompts/prompt-1/test-runs");
        });
    }

    [Fact]
    public void PromptManagementActivatesPromptWithOneClick()
    {
        var activationRequests = 0;
        var prompts = new[]
        {
            new ScoringPromptDto("prompt-2", "job-1", 2, "Current", "active", DateTime.UtcNow, DateTime.UtcNow, "admin", null, null, "generated", null, null, "o3", "high"),
            new ScoringPromptDto("prompt-1", "job-1", 1, "Draft", "draft", DateTime.UtcNow, DateTime.UtcNow, "admin", null, null, "manual", null, null, "o3", "maximum"),
        };
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/jobs/job-1/prompts/prompt-1/activate")
            {
                activationRequests++;
                return JsonResponse(prompts[1] with { Status = "active" });
            }

            return path switch
            {
                "/api/jobs/job-1/prompts" => JsonResponse(prompts),
                "/api/reasoning-models" => JsonResponse(ReasoningCatalog()),
                "/api/jobs/job-1/prompt-generation-instructions" =>
                    JsonResponse(Array.Empty<PromptGenerationInstructionDto>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<PromptManagement>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.HasApprovedRubric, true));

        cut.WaitForAssertion(() => cut.Find("button[title='Activate']"));
        cut.Find("button[title='Activate']").Click();

        cut.WaitForAssertion(() =>
        {
            activationRequests.Should().Be(1);
            var rows = cut.FindAll("tbody tr");
            rows.Single(row => row.TextContent.Contains("v1"))
                .TextContent.Should().Contain("active");
            rows.Single(row => row.TextContent.Contains("v2"))
                .TextContent.Should().Contain("inactive");
        });
    }

    [Fact]
    public void PromptManagementAddsCommentWithoutChangingRating()
    {
        var prompt = new ScoringPromptDto(
            "prompt-1", "job-1", 1, "Prompt", "active",
            DateTime.UtcNow, DateTime.UtcNow, "admin", 4, "Original",
            "manual", null, null, "o3", "high");
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/jobs/job-1/prompts/prompt-1/rate")
            {
                return JsonResponse(prompt with { Comments = "Reviewed after testing" });
            }

            return path switch
            {
                "/api/jobs/job-1/prompts" => JsonResponse(new[] { prompt }),
                "/api/reasoning-models" => JsonResponse(ReasoningCatalog()),
                "/api/jobs/job-1/prompt-generation-instructions" =>
                    JsonResponse(Array.Empty<PromptGenerationInstructionDto>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<PromptManagement>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.HasApprovedRubric, true));

        cut.WaitForAssertion(() => cut.Find("button[title='Add or edit comment']"));
        cut.Find("button[title='Add or edit comment']").Click();
        cut.Find("textarea[aria-label='Comment for prompt v1']")
            .Change("Reviewed after testing");
        cut.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Save Comment")
            .Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("textarea[aria-label='Comment for prompt v1']").Should().BeEmpty();
            cut.Find("tbody tr").TextContent.Should().Contain("⭐ 4/5");
        });
    }

    [Fact]
    public void JobDetail_TestsTheActivePromptInsteadOfTheOlderProductionPrompt()
    {
        var activePrompt = new ScoringPromptDto(
            "prompt-luna",
            "job-1",
            7,
            "Luna prompt",
            "active",
            DateTime.UtcNow,
            DateTime.UtcNow,
            "admin",
            null,
            null,
            "manual",
            null,
            null,
            "gpt-5.6-luna",
            "high");
        var productionPrompt = new ScoringPromptDto(
            "prompt-o3",
            "job-1",
            6,
            "O3 prompt",
            "production-approved",
            DateTime.UtcNow,
            DateTime.UtcNow,
            "admin",
            null,
            null,
            "manual",
            null,
            null,
            "o3",
            "high",
            "test-o3",
            "o3",
            "high");
        var config = new JobConfigDto(
            """[{"name":"Technical","weight":1,"description":"Technical"}]""",
            "[]",
            "[]",
            1,
            "median",
            60,
            80,
            15,
            "approved");
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/auth/me" => JsonResponse(new UserInfo(
                    "user-1",
                    "admin",
                    "admin",
                    "Engineering",
                    "Admin",
                    "admin@example.com")),
                "/api/jobs/job-1" => JsonResponse(new JobDto(
                    "job-1",
                    "JOB-1",
                    "Test Job",
                    "Engineering",
                    "TalentMatch",
                    DateTime.UtcNow,
                    "active",
                    "config-1",
                    "Description",
                    "user-1",
                    DateTime.UtcNow)),
                "/api/jobs/job-1/applications" => JsonResponse(Array.Empty<ApplicationDto>()),
                "/api/jobs/job-1/config" => JsonResponse(config),
                "/api/jobs/job-1/prompts" => JsonResponse(new[] { activePrompt, productionPrompt }),
                "/api/jobs/job-1/prompts/prompt-o3/profile" => JsonResponse(
                    new PromptProfileStatusDto(
                        "prompt-o3",
                        "o3",
                        "high",
                        "o3",
                        "high",
                        true,
                        true,
                        "test-o3",
                        null)),
                "/api/jobs/job-1/prompts/prompt-luna/profile" => JsonResponse(
                    new PromptProfileStatusDto(
                        "prompt-luna",
                        "gpt-5.6-luna",
                        "high",
                        "gpt-5.6-luna",
                        "high",
                        false,
                        false,
                        null,
                        "Testing required")),
                "/api/jobs/job-1/prompts/prompt-luna/test-runs" =>
                    JsonResponse(Array.Empty<PromptTestRunDto>()),
                "/api/jobs/job-1/prompts/prompt-o3/test-runs" =>
                    JsonResponse(Array.Empty<PromptTestRunDto>()),
                "/api/jobs/job-1/prompt-generation-instructions" =>
                    JsonResponse(Array.Empty<PromptGenerationInstructionDto>()),
                "/api/reasoning-models" => JsonResponse(new ReasoningModelsResponseDto(
                    "o3",
                    "high",
                    ["low", "medium", "high"],
                    [
                        new ReasoningModelOptionDto("reason01", "o3", true),
                        new ReasoningModelOptionDto("reason02", "gpt-5.6-luna", false)
                    ])),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var api = new ApiClient(httpClient);
        Services.AddSingleton(api);
        Services.AddSingleton(new UploadCoordinator(api));

        using var cut = Render<JobDetail>(parameters => parameters
            .Add(component => component.JobId, "job-1"));
        cut.WaitForAssertion(() => cut.FindAll(".action-row button")
            .Should().Contain(button => button.TextContent.Trim() == "Manage Prompts"));
        cut.FindAll(".action-row button")
            .Single(button => button.TextContent.Trim() == "Manage Prompts")
            .Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindComponents<PromptTestRunner>().Should().BeEmpty();
            cut.Find("button[aria-label='View prompt v7 test details and history']");
        });
        cut.Find("button[aria-label='View prompt v7 test details and history']").Click();

        cut.WaitForAssertion(() =>
        {
            var runner = cut.FindComponent<PromptTestRunner>();
            runner.Instance.ActivePrompt.Should().NotBeNull();
            runner.Instance.ActivePrompt!.Id.Should().Be("prompt-luna");
            runner.Markup.Should().Contain("New tests will use prompt: v7 (active)");
            runner.Markup.Should().Contain("gpt-5.6-luna / high");
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
        var jobApi = new ApiClient(httpClient);
        Services.AddSingleton(jobApi);
        Services.AddSingleton(new UploadCoordinator(jobApi));

        using var cut = Render<JobDetail>(parameters => parameters
            .Add(component => component.JobId, "job-1"));

        cut.WaitForAssertion(() => cut.Find(".pipeline-section"));
        cut.Find("details.pipeline-results").HasAttribute("open").Should().BeTrue();
        cut.FindAll(".action-row button").Single(button => button.TextContent.Trim() == "Help").Click();

        cut.WaitForAssertion(() =>
        {
            var sections = cut.FindAll(".pipeline-section, .job-help, .job-collapsible");
            sections.First().ClassList.Should().Contain("pipeline-section");
            sections[^2].TextContent.Should().Contain("Extraction diagnostics");
            sections.Last().TextContent.Should().Contain("Upload activity");
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

    private sealed class AsyncRoutingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            route(request, cancellationToken);
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };

    private static ReasoningModelsResponseDto ReasoningCatalog()
        => new(
            "o3",
            "high",
            ["low", "medium", "high", "maximum"],
            [new ReasoningModelOptionDto("reason01", "o3", true)]);
}
