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
    public void PromptRevisionSelectorLoadsHistoricalTestRuns()
    {
        var activePrompt = Prompt(
            "prompt-luna",
            7,
            "active",
            "gpt-5.6-luna",
            "high");
        var productionPrompt = Prompt(
            "prompt-o3",
            6,
            "production-approved",
            "o3",
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
                    JsonResponse(new[] { historicalRun }),
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
            .Add(component => component.ActivePrompt, activePrompt)
            .Add(component => component.Prompts, new[] { activePrompt, productionPrompt }));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("New tests will use prompt: v7 (active)");
            cut.Find("select[aria-label='Prompt revision for test results']")
                .Children.Should().HaveCount(2);
        });

        cut.Find("select[aria-label='Prompt revision for test results']")
            .Change("prompt-o3");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("New tests will use prompt: v6 (production-approved)");
            cut.Markup.Should().Contain("historic...");
            cut.Markup.Should().Contain("Profile: o3 / high");
            cut.Markup.Should().Contain("View Results");
            var historyGroup = cut.Find("details.prompt-test-history-group");
            historyGroup.HasAttribute("open").Should().BeTrue();
            historyGroup.QuerySelector("summary")!.TextContent
                .Should().Contain("Prompt v6").And.Contain("1 test run(s)");
        });
    }

    [Fact]
    public void FailedMustHaveWithExplanatoryEvidenceRendersAsFailed()
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
            """{"Mandatory Requirements":40,"Technical":55,"Administration":15}""",
            """
            {"passed":false,"missing_criteria":["Registered engineer"],"details":{"entries":[
              {"criterion":"Registered engineer","passed":false,"evidence":"Registration could not be verified"}
            ]}}
            """,
            """
            [
              {"category":"Mandatory Requirements","snippet":"Registration could not be verified"},
              {"category":"Mandatory Requirements","snippet":"Kubernetes experience listed"},
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
                {"id":"mandatory","name":"Mandatory Requirements","weight":0.5,"description":"Mandatory requirements","order":0},
                {"id":"technical","name":"Technical","weight":0.3,"description":"Technical requirements","order":1},
                {"id":"administration","name":"Administration","weight":0.2,"description":"Administrative duties","order":2}
              ],
              "items":[
                {"id":"item-1","categoryId":"mandatory","text":"Registered engineer","requirementType":"must_have","order":0,"reviewStatus":"confirmed","createdFrom":"manual"},
                {"id":"item-2","categoryId":"technical","text":"Kubernetes","requirementType":"desired","order":0,"reviewStatus":"confirmed","createdFrom":"manual"}
              ]
            }
            """,
            """[{"criterion":"Registered engineer","description":""}]""",
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
            var registered = cut.FindAll("li")
                .Where(item => item.TextContent.Contains("Registered engineer"))
                .ToList();
            var kubernetes = cut.FindAll("li")
                .Where(item => item.TextContent.Contains("Kubernetes"))
                .ToList();
            var administration = cut.FindAll("li")
                .Where(item => item.TextContent.Contains("No budgeting or resource management evidence"))
                .ToList();
            registered.Should().NotBeEmpty().And.OnlyContain(
                item => item.TextContent.TrimStart().StartsWith("❌"));
            kubernetes.Should().NotBeEmpty().And.OnlyContain(
                item => item.TextContent.TrimStart().StartsWith("❌"));
            administration.Should().ContainSingle()
                .Which.TextContent.TrimStart().Should().StartWith("❌");
            cut.Markup.Should().Contain("Weight: 50%");
            cut.Markup.Should().Contain("Weight: 30%");
            cut.Markup.Should().Contain("Weight: 20%");
            cut.Markup.Should().Contain("Registration could not be verified");
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
