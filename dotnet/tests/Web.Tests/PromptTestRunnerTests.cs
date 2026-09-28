using System.Net;
using System.Net.Http.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class PromptTestRunnerTests : BunitContext
{
    [Fact]
    public void ProductionApprovedPromptRemainsAvailableForPromptTesting()
    {
        var productionPrompt = Prompt(
            "prompt-production",
            4,
            "production-approved",
            "o3",
            "high");
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/jobs/job-1/config" => JsonResponse<JobConfigDto?>(null),
                "/api/jobs/job-1/prompts/prompt-production/test-runs" =>
                    JsonResponse(Array.Empty<PromptTestRunDto>()),
                "/api/jobs/job-1/prompts/prompt-production/profile" =>
                    JsonResponse(Profile("prompt-production", "o3", true)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<PromptTestRunner>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.ActivePrompt, productionPrompt));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain(
                "New tests will use prompt: v4 (production-approved)");
            cut.FindAll("button").Should().Contain(
                button => button.TextContent.Contains("Upload Test Applications"));
        });
    }

    [Fact]
    public async Task UnchangedParentRendersDoNotReloadPromptTestContext()
    {
        var configRequests = 0;
        var testRunRequests = 0;
        var activePrompt = Prompt("prompt-active", 4, "active", "o3", "high");
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/jobs/job-1/config")
            {
                configRequests++;
                return JsonResponse<JobConfigDto?>(null);
            }

            if (path.EndsWith("/test-runs", StringComparison.Ordinal))
            {
                testRunRequests++;
                return JsonResponse(Array.Empty<PromptTestRunDto>());
            }

            if (path == "/api/jobs/job-1/prompts/prompt-active/profile")
                return JsonResponse(Profile("prompt-active", "o3", false));

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<PromptTestRunner>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.ActivePrompt, activePrompt));

        cut.WaitForAssertion(() =>
        {
            configRequests.Should().Be(1);
            testRunRequests.Should().Be(1);
        });

        var lifecycleMethod = typeof(PromptTestRunner).GetMethod(
            "OnParametersSetAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        lifecycleMethod.Should().NotBeNull();
        await (Task)lifecycleMethod!.Invoke(cut.Instance, null)!;

        configRequests.Should().Be(1);
        testRunRequests.Should().Be(1);
    }

    [Fact]
    public void SelectedPromptLoadsOnlyItsOwnTestHistory()
    {
        var historicalHistoryRequests = 0;
        var activePrompt = Prompt(
            "prompt-luna",
            7,
            "active",
            "gpt-5.6-luna",
            "high");
        var historicalRun = new PromptTestRunDto(
            "historical-test-0001",
            "job-1",
            "prompt-o3",
            "approved",
            """["application-1"]""",
            DateTime.UtcNow,
            DateTime.UtcNow,
            "reviewer",
            null,
            "o3",
            "high",
            "o3",
            "high");
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/jobs/job-1/config" => JsonResponse<JobConfigDto?>(null),
                "/api/jobs/job-1/prompts/prompt-luna/test-runs" =>
                    JsonResponse(Array.Empty<PromptTestRunDto>()),
                "/api/jobs/job-1/prompts/prompt-o3/test-runs" =>
                    CountHistoricalRequest(),
                "/api/jobs/job-1/prompts/prompt-luna/profile" => JsonResponse(
                    Profile("prompt-luna", "gpt-5.6-luna", false)),
                "/api/jobs/job-1/prompts/prompt-o3/profile" => JsonResponse(
                    Profile("prompt-o3", "o3", true)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<PromptTestRunner>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.ActivePrompt, activePrompt));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("New tests will use prompt: v7 (active)");
            cut.FindAll("select[aria-label='Prompt revision for test results']").Should().BeEmpty();
            cut.Markup.Should().NotContain("historic...");
            cut.Markup.Should().NotContain("Prompt v6");
            historicalHistoryRequests.Should().Be(0);
        });

        HttpResponseMessage CountHistoricalRequest()
        {
            historicalHistoryRequests++;
            return JsonResponse(new[] { historicalRun });
        }
    }

    [Fact]
    public void PromptTestUsesSharedScoringRunDetails()
    {
        var prompt = new ScoringPromptDto(
            "prompt-1",
            "job-1",
            1,
            "Score the candidate",
            "active",
            DateTime.UtcNow,
            DateTime.UtcNow,
            "admin",
            null,
            null,
            "manual",
            null,
            null,
            "o3",
            "high");
        var testRun = new PromptTestRunDto(
            "test-run-0001",
            "job-1",
            "prompt-1",
            "pending_review",
            """["application-1"]""",
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            null,
            "o3",
            "high");
        var application = new ApplicationDto(
            "application-1",
            "job-1",
            "candidate-1",
            "Candidate One",
            null,
            "Completed",
            40,
            "Excluded",
            0,
            DateTime.UtcNow);
        var scoringRun = new ScoringRunDto(
            "run-1",
            1,
            40,
            """{"Qualifications and Experience":40,"Technical":55,"Administration":15}""",
            """
            {"Passed":false,"Missing_Criteria":["Registered engineer"],"Details":{"Entries":[
              {"Criterion":"Registered engineer","Passed":false,"Evidence":"Registration could not be verified"},
              {"Criterion":"Basic Literacy","Passed":true,"Evidence":"Grade 10, which exceeds Grade 8"}
            ]}}
            """,
            """
            [
              {"category":"Qualifications and Experience","snippet":"Registration could not be verified"},
              {"category":"Qualifications and Experience","snippet":"Kubernetes experience listed"},
              {"category":"Administration","snippet":"No budgeting or resource management evidence"}
            ]
            """,
            "[]",
            "o3",
            "prompt-1",
            0,
            0,
            DateTime.UtcNow,
            "high");
        var detail = new PromptTestRunDetailDto(
            testRun,
            [new TestRunApplicationDetailDto(application, [scoringRun])]);
        var config = new JobConfigDto(
            """
            {
              "schemaVersion":"rubric-v2",
              "categories":[
                {"id":"qualifications","name":"Qualifications and Experience","weight":0.5,"description":"Qualifications and experience","order":0},
                {"id":"technical","name":"Technical","weight":0.3,"description":"Technical requirements","order":1},
                {"id":"administration","name":"Administration","weight":0.2,"description":"Administrative duties","order":2}
              ],
              "items":[
                {"id":"item-1","categoryId":"qualifications","text":"Registered engineer","requirementType":"must_have","order":0,"reviewStatus":"confirmed","createdFrom":"manual"},
                {"id":"item-2","categoryId":"qualifications","text":"Basic Literacy","requirementType":"must_have","order":1,"reviewStatus":"confirmed","createdFrom":"manual"},
                {"id":"item-3","categoryId":"technical","text":"Kubernetes","requirementType":"desired","order":0,"reviewStatus":"confirmed","createdFrom":"manual"}
              ]
            }
            """,
            """[{"criterion":"Registered engineer","description":""},{"criterion":"Basic Literacy","description":""}]""",
            "[]",
            1,
            "median",
            60,
            80,
            15);

        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/jobs/job-1/config" => JsonResponse(config),
                "/api/jobs/job-1/prompts/prompt-1/test-runs" => JsonResponse(new[] { testRun }),
                "/api/jobs/job-1/prompts/prompt-1/profile" => JsonResponse(
                    new PromptProfileStatusDto(
                        "prompt-1",
                        "o3",
                        "high",
                        "o3",
                        "high",
                        false,
                        false,
                        null,
                        "Testing required")),
                "/api/jobs/job-1/prompts/prompt-1/test-runs/test-run-0001" => JsonResponse(detail),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        using var cut = Render<PromptTestRunner>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.ActivePrompt, prompt));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("View Results"));
        cut.FindAll("button").Single(button => button.TextContent.Contains("View Results")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<ScoringRunDetails>();
            cut.FindAll("[aria-label='Failed']").Should().ContainSingle();
            cut.FindAll("[aria-label='Passed']").Should().ContainSingle();
            cut.Markup.Should().Contain("Registered engineer");
            cut.Markup.Should().Contain("Basic Literacy");
            cut.Markup.Should().Contain("Grade 10, which exceeds Grade 8");
            cut.Markup.Should().Contain("Registration could not be verified");
            cut.Markup.Should().Contain("Qualifications and Experience");
            cut.Markup.Should().NotContain("Category Score Breakdown (scored JSON)");
        });
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };

    private static ScoringPromptDto Prompt(
        string id,
        int version,
        string status,
        string model,
        string reasoning)
        => new(
            id,
            "job-1",
            version,
            "Score the candidate",
            status,
            DateTime.UtcNow,
            DateTime.UtcNow,
            "admin",
            null,
            null,
            "manual",
            null,
            null,
            model,
            reasoning);

    private static PromptProfileStatusDto Profile(
        string promptId,
        string model,
        bool approved)
        => new(
            promptId,
            model,
            "high",
            model,
            "high",
            approved,
            approved,
            approved ? "historical-test-0001" : null,
            approved ? null : "Testing required");

    private sealed class RoutingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
