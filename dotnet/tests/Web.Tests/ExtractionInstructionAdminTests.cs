using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Layout;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class ExtractionInstructionAdminTests : BunitContext
{
    [Fact]
    public void Page_RendersVersionsAndCreateForm()
    {
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/auth/me", HttpStatusCode.OK, """
            { "id":"admin-1", "username":"admin", "role":"admin", "department":"all", "fullName":"Administrator", "email":"admin@example.com" }
            """);
        handler.SetupResponse("GET", "/api/admin/extraction-instructions", HttpStatusCode.OK, """
            [
              {
                "id":"instruction-v1",
                "versionNumber":1,
                "instructionText":"Extract requirements individually.",
                "protectedContractVersion":"extraction-rubric-v1",
                "status":"active",
                "validationStatus":"valid",
                "changeNote":null,
                "validationFindings":[],
                "createdAt":"2026-09-09T00:00:00Z",
                "createdBy":"seed",
                "validatedAt":"2026-09-09T00:00:00Z",
                "validatedBy":"seed",
                "activatedAt":"2026-09-09T00:00:00Z",
                "activatedBy":"seed",
                "concurrencyVersion":1
              }
            ]
            """);
        handler.SetupResponse("GET", "/api/admin/extraction-instructions/instruction-v1", HttpStatusCode.OK, """
            {
              "id":"instruction-v1",
              "versionNumber":1,
              "instructionText":"Extract requirements individually.",
              "protectedContractVersion":"extraction-rubric-v1",
              "status":"active",
              "validationStatus":"valid",
              "changeNote":null,
              "validationFindings":[],
              "protectedContract":{"version":"extraction-rubric-v1"},
              "createdAt":"2026-09-09T00:00:00Z",
              "createdBy":"seed",
              "validatedAt":"2026-09-09T00:00:00Z",
              "validatedBy":"seed",
              "activatedAt":"2026-09-09T00:00:00Z",
              "activatedBy":"seed",
              "concurrencyVersion":1
            }
            """);
        handler.SetupResponse("GET", "/api/admin/prompt-generation-instructions", HttpStatusCode.OK, """
            [
              {
                "id":"scoring-instruction-v1",
                "jobId":null,
                "versionNumber":1,
                "instructionText":"Generate an evidence-based candidate scoring prompt.",
                "status":"active",
                "changeNote":"Initial default",
                "createdAt":"2026-09-17T00:00:00Z",
                "createdBy":"seed",
                "activatedAt":"2026-09-17T00:00:00Z",
                "activatedBy":"seed"
              }
            ]
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ExtractionInstructions>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Job Extraction Prompt");
            cut.Markup.Should().Contain("Extract requirements individually.");
            cut.Markup.Should().Contain("Create draft");
            cut.Markup.Should().Contain("How to activate a draft prompt");
            cut.Markup.Should().Contain("The draft must show a validation status of");
            cut.Markup.Should().NotContain("Activation disabled:");
            cut.Markup.Should().NotContain("System Scoring Prompt Generation");
        });
    }

    [Fact]
    public void ScoringPromptGenerationPage_RendersSystemInstructionVersions()
    {
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/admin/prompt-generation-instructions", HttpStatusCode.OK, """
            [
              {
                "id":"scoring-instruction-v1",
                "jobId":null,
                "versionNumber":1,
                "instructionText":"Generate an evidence-based candidate scoring prompt.",
                "status":"active",
                "changeNote":"Initial default",
                "createdAt":"2026-09-17T00:00:00Z",
                "createdBy":"seed",
                "activatedAt":"2026-09-17T00:00:00Z",
                "activatedBy":"seed"
              }
            ]
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ScoringPromptGeneration>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("System Scoring Prompt Generation");
            cut.Markup.Should().Contain("Generate an evidence-based candidate scoring prompt.");
            cut.Markup.Should().NotContain("Job Extraction Prompt");
        });
    }

    [Fact]
    public void Page_ShowsIndicatorWhileValidationIsUnderway()
    {
        var handler = new DelayedValidationHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ExtractionInstructions>();
        cut.WaitForElement("input[type='file']");
        cut.Markup.Should().Contain("Activation disabled:");
        cut.Markup.Should().Contain("validate this draft with a representative job specification first");

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("sample job specification", "sample-spec.txt", contentType: "text/plain"));

        cut.WaitForAssertion(() =>
        {
            var indicator = cut.Find("[role='status']");
            indicator.TextContent.Should().Contain("Validation underway");
            indicator.GetAttribute("aria-live").Should().Be("polite");
            var progress = cut.Find("progress[aria-label='Job extraction prompt validation progress']");
            progress.GetAttribute("max").Should().Be("360");
        });
    }

    [Fact]
    public void Page_EnablesActivationAndShowsSuccessBesideValidationControl()
    {
        var handler = new SuccessfulValidationHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ExtractionInstructions>();
        cut.WaitForElement("input[type='file']");

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("sample job specification", "sample-spec.txt", contentType: "text/plain"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-validation-result]").TextContent.Should()
                .Contain("Validation passed. The draft can now be activated.");
            cut.FindAll("button").Single(button => button.TextContent.Trim() == "Activate")
                .HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public void Page_ShowsValidationReasonsAndSuggestedFixes()
    {
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/admin/extraction-instructions", HttpStatusCode.OK, """
            [{
              "id":"instruction-v2",
              "versionNumber":2,
              "instructionText":"Extract every requirement.",
              "protectedContractVersion":"extraction-rubric-v1",
              "status":"draft",
              "validationStatus":"invalid",
              "validationFindings":[{
                "code":"invalid_json",
                "severity":"error",
                "path":"$",
                "message":"The extraction response was empty."
              }],
              "createdAt":"2026-09-10T00:00:00Z",
              "createdBy":"admin",
              "concurrencyVersion":2
            }]
            """);
        handler.SetupResponse("GET", "/api/admin/extraction-instructions/instruction-v2", HttpStatusCode.OK, """
            {
              "id":"instruction-v2",
              "versionNumber":2,
              "instructionText":"Extract every requirement.",
              "protectedContractVersion":"extraction-rubric-v1",
              "status":"draft",
              "validationStatus":"invalid",
              "validationFindings":[{
                "code":"invalid_json",
                "severity":"error",
                "path":"$",
                "message":"The extraction response was empty."
              }],
              "protectedContract":{"version":"extraction-rubric-v1"},
              "createdAt":"2026-09-10T00:00:00Z",
              "createdBy":"admin",
              "concurrencyVersion":2
            }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ExtractionInstructions>();

        cut.WaitForAssertion(() =>
        {
            var findings = cut.Find("[aria-label='Validation findings']");
            findings.TextContent.Should().Contain("Why validation did not pass");
            findings.TextContent.Should().Contain("The extraction response was empty.");
            findings.TextContent.Should().Contain("Suggested fix:");
            findings.TextContent.Should().Contain("AWReason service produced no model output");
        });
    }

    [Fact]
    public void Page_RendersLoadingStateBeforeInstructionRequestCompletes()
    {
        var httpClient = new HttpClient(new YieldingInstructionHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new ApiClient(httpClient));

        var cut = Render<ExtractionInstructions>();

        cut.Markup.Should().Contain("Job Extraction Prompt");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Create draft"));
    }

    [Fact]
    public void NavMenu_ShowsCollapsedSystemConfigurationWithSeparatePromptLinks()
    {
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/auth/me", HttpStatusCode.OK, """
            { "id":"admin-1", "username":"admin", "role":"admin", "department":"all", "fullName":"Administrator", "email":"admin@example.com" }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));
        Services.AddSingleton(PublicAuthConfiguration.Simple("http://localhost/"));

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() =>
        {
            var configurationMenu = cut.Find("details.nav-group");
            configurationMenu.TextContent.Should().Contain("System Configuration");
            configurationMenu.HasAttribute("open").Should().BeFalse();
            var extractionLink = configurationMenu.QuerySelector("a[href='extraction-instructions']");
            extractionLink.Should().NotBeNull();
            extractionLink!.TextContent.Should().Contain("Job Extraction Prompt");
            var scoringLink = configurationMenu.QuerySelector("a[href='scoring-prompt-generation']");
            scoringLink.Should().NotBeNull();
            scoringLink!.TextContent.Should().Contain("Scoring Prompt Generation");
        });
    }

    private sealed class YieldingInstructionHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        }
    }

    private sealed class DelayedValidationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get
                && path == "/api/admin/extraction-instructions")
            {
                return JsonResponse("""
                    [{
                      "id":"instruction-v2",
                      "versionNumber":2,
                      "instructionText":"Extract every requirement.",
                      "protectedContractVersion":"extraction-rubric-v1",
                      "status":"draft",
                      "validationStatus":"unvalidated",
                      "validationFindings":[],
                      "createdAt":"2026-09-10T00:00:00Z",
                      "createdBy":"admin",
                      "concurrencyVersion":1
                    }]
                    """);
            }

            if (request.Method == HttpMethod.Get
                && path == "/api/admin/extraction-instructions/instruction-v2")
            {
                return JsonResponse("""
                    {
                      "id":"instruction-v2",
                      "versionNumber":2,
                      "instructionText":"Extract every requirement.",
                      "protectedContractVersion":"extraction-rubric-v1",
                      "status":"draft",
                      "validationStatus":"unvalidated",
                      "validationFindings":[],
                      "protectedContract":{"version":"extraction-rubric-v1"},
                      "createdAt":"2026-09-10T00:00:00Z",
                      "createdBy":"admin",
                      "concurrencyVersion":1
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post
                && path == "/api/admin/extraction-instructions/instruction-v2/validate")
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
    }

    private sealed class SuccessfulValidationHandler : HttpMessageHandler
    {
        private bool validated;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/admin/extraction-instructions/instruction-v2/validate")
            {
                validated = true;
                return Task.FromResult(JsonResponse("""
                    {"validationStatus":"valid","validationFindings":[]}
                    """));
            }

            if (request.Method == HttpMethod.Get
                && path == "/api/admin/extraction-instructions")
            {
                return Task.FromResult(JsonResponse($$"""
                    [{
                      "id":"instruction-v2",
                      "versionNumber":2,
                      "instructionText":"Extract every requirement.",
                      "protectedContractVersion":"extraction-rubric-v1",
                      "status":"draft",
                      "validationStatus":"{{(validated ? "valid" : "unvalidated")}}",
                      "validationFindings":[],
                      "createdAt":"2026-09-10T00:00:00Z",
                      "createdBy":"admin",
                      "concurrencyVersion":{{(validated ? 2 : 1)}}
                    }]
                    """));
            }

            if (request.Method == HttpMethod.Get
                && path == "/api/admin/extraction-instructions/instruction-v2")
            {
                return Task.FromResult(JsonResponse($$"""
                    {
                      "id":"instruction-v2",
                      "versionNumber":2,
                      "instructionText":"Extract every requirement.",
                      "protectedContractVersion":"extraction-rubric-v1",
                      "status":"draft",
                      "validationStatus":"{{(validated ? "valid" : "unvalidated")}}",
                      "validationFindings":[],
                      "protectedContract":{"version":"extraction-rubric-v1"},
                      "createdAt":"2026-09-10T00:00:00Z",
                      "createdBy":"admin",
                      "concurrencyVersion":{{(validated ? 2 : 1)}}
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
    }
}
