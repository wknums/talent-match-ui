using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class LegacyRubricConversionTests : BunitContext
{
    [Fact]
    public async Task Preview_ReturnsReviewableRubricV2Proposal_WithoutMutatingCurrentConfig()
    {
        using var factory = new LegacyConversionFactory();
        using var client = factory.CreateAuthenticatedClient();
        await LoginAsAdmin(client);

        var preview = await client.PostAsJsonAsync("/api/jobs/job-legacy/rubric/convert", new
        {
            mode = "preview",
            expectedConfigVersionId = "cfg-legacy"
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await preview.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("schemaVersion").GetString().Should().Be("rubric-v2");
        payload.GetProperty("legacySourceVersionId").GetString().Should().Be("cfg-legacy");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Jobs.Single(job => job.Id == "job-legacy").CurrentConfigVersionId.Should().Be("cfg-legacy");
        db.JobConfigVersions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Confirm_CreatesNewConfigVersion_FromReviewedProposal()
    {
        using var factory = new LegacyConversionFactory();
        using var client = factory.CreateAuthenticatedClient();
        await LoginAsAdmin(client);

        var preview = await client.PostAsJsonAsync("/api/jobs/job-legacy/rubric/convert", new
        {
            mode = "preview",
            expectedConfigVersionId = "cfg-legacy"
        });
        var reviewedRubric = await preview.Content.ReadFromJsonAsync<JsonElement>();

        var confirm = await client.PostAsJsonAsync("/api/jobs/job-legacy/rubric/convert", new
        {
            mode = "confirm",
            expectedConfigVersionId = "cfg-legacy",
            reviewedRubric
        });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.JobConfigVersions.Should().HaveCount(2);
        var job = await db.Jobs.Include(candidate => candidate.ConfigVersions).SingleAsync(candidate => candidate.Id == "job-legacy");
        job.CurrentConfigVersionId.Should().NotBe("cfg-legacy");
        var current = job.ConfigVersions.Single(candidate => candidate.Id == job.CurrentConfigVersionId);
        current.RubricJson.Should().Contain("\"schemaVersion\":\"rubric-v2\"");
    }

    [Fact]
    public void EditDialog_SaveFailure_PreservesReorderedRubricState()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<int>("eval").SetResult(1200);

        var handler = new MockHttpHandler();
        handler.SetupResponse("PUT", "/api/jobs/job-1/config", HttpStatusCode.Conflict, """
            { "error": "stale_version", "message": "stale_version: reload before saving." }
            """);
        handler.SetupResponse("GET", "/api/jobs/job-1/config", HttpStatusCode.OK, """
            {
              "rubricJson":"[]",
              "mustHavesJson":"[]",
              "desiredCriteriaJson":"[]",
              "scoringRunCount":3,
              "aggregationStrategy":"median",
              "longlistThreshold":70,
              "shortlistThreshold":85,
              "varianceThreshold":15,
              "rubricApprovalStatus":"draft",
              "id":"cfg-2",
              "versionNumber":2
            }
            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));
        Services.AddSingleton(PublicAuthConfiguration.Simple("http://localhost/"));

        var cut = Render<CreateJobDialog>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.EditingJob, new JobDto("job-1", "JOB-1", "Data Engineer", "Engineering", "Northwind", DateTime.UtcNow, "active", "cfg-1", "desc", "admin", DateTime.UtcNow))
            .Add(component => component.EditingConfig, new JobConfigDto(
                """{"schemaVersion":"rubric-v2","categories":[{"id":"cat-1","name":"Technical Skills","weight":1,"description":"desc","order":0}],"items":[{"id":"item-1","categoryId":"cat-1","text":"Expert SQL experience","requirementType":"must_have","order":0,"sourceText":"Expert SQL experience","sourceLocation":null,"sourceRequirementId":"req-1","reviewStatus":"confirmed","createdFrom":"extracted"},{"id":"item-2","categoryId":"cat-1","text":"Expert Python experience","requirementType":"must_have","order":1,"sourceText":"Expert Python experience","sourceLocation":null,"sourceRequirementId":"req-2","reviewStatus":"confirmed","createdFrom":"extracted"}]}""",
                """[{"criterion":"Expert SQL experience","description":"Expert SQL experience"}]""",
                "[]",
                3,
                "median",
                70,
                85,
                15,
                "draft",
                "extracted",
                "extract-1",
                "instruction-v1",
                "cfg-1",
                1))
            .Add(component => component.OnUpdated, EventCallback.Factory.Create(this, () => Task.CompletedTask))
            .Add(component => component.OnCancel, EventCallback.Factory.Create(this, () => Task.CompletedTask)));

        cut.Find("button[data-action='move-down']").Click();
        cut.FindAll("button").Single(button => button.TextContent.Contains("Update Configuration")).Click();

        cut.Markup.Should().Contain("Your changes are preserved");
        cut.FindAll("button[data-edit-item]").Select(button => button.GetAttribute("data-edit-item"))
            .Should().Equal("item-2", "item-1");
    }

    [Fact]
    public void EditDialog_StaleSave_RefreshesVersionAndRetriesOnlyAfterUserConfirms()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<int>("eval").SetResult(1200);

        var handler = new StaleThenSuccessConfigHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new ApiClient(httpClient));
        Services.AddSingleton(PublicAuthConfiguration.Simple("http://localhost/"));
        var updatedCount = 0;

        var cut = Render<CreateJobDialog>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.EditingJob, new JobDto("job-1", "JOB-1", "Data Engineer", "Engineering", "Northwind", DateTime.UtcNow, "active", "cfg-1", "desc", "admin", DateTime.UtcNow))
            .Add(component => component.EditingConfig, new JobConfigDto(
                """[{"name":"Technical Skills","weight":1,"description":"Required skills"}]""",
                "[]",
                "[]",
                3,
                "median",
                70,
                85,
                15,
                "draft",
                "manual",
                Id: "cfg-1",
                VersionNumber: 1))
            .Add(component => component.OnUpdated, EventCallback.Factory.Create(this, () => updatedCount++))
            .Add(component => component.OnCancel, EventCallback.Factory.Create(this, () => Task.CompletedTask)));

        var updateButton = cut.FindAll("button").Single(button => button.TextContent.Contains("Update Configuration"));
        updateButton.Click();

        cut.WaitForAssertion(() =>
        {
            handler.ExpectedVersionIds.Should().Equal("cfg-1");
            cut.Markup.Should().Contain("click Update Configuration again");
            updatedCount.Should().Be(0);
        });

        cut.FindAll("button").Single(button => button.TextContent.Contains("Update Configuration")).Click();

        cut.WaitForAssertion(() =>
        {
            handler.ExpectedVersionIds.Should().Equal("cfg-1", "cfg-2");
            updatedCount.Should().Be(1);
        });
    }

    private static async Task LoginAsAdmin(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "adm1n99" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class StaleThenSuccessConfigHandler : HttpMessageHandler
    {
        public List<string?> ExpectedVersionIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/jobs/job-1/config")
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "rubricJson":"[{\"name\":\"Technical Skills\",\"weight\":1,\"description\":\"Required skills\"}]",
                      "mustHavesJson":"[]",
                      "desiredCriteriaJson":"[]",
                      "scoringRunCount":3,
                      "aggregationStrategy":"median",
                      "longlistThreshold":70,
                      "shortlistThreshold":85,
                      "varianceThreshold":15,
                      "rubricApprovalStatus":"draft",
                      "id":"cfg-2",
                      "versionNumber":2
                    }
                    """);
            }

            if (request.Method == HttpMethod.Put && request.RequestUri?.AbsolutePath == "/api/jobs/job-1/config")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                ExpectedVersionIds.Add(document.RootElement.GetProperty("expectedConfigVersionId").GetString());

                return ExpectedVersionIds.Count == 1
                    ? JsonResponse(HttpStatusCode.Conflict, """
                        { "error": "stale_version", "message": "stale_version: reload before saving." }
                        """)
                    : JsonResponse(HttpStatusCode.OK, "{}");
            }

            return JsonResponse(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
            => new(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
    }

    private sealed class LegacyConversionFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                db.Users.Add(new TalentMatch.Domain.Entities.User
                {
                    Username = "admin",
                    Role = "admin",
                    FullName = "Administrator",
                    Department = "all",
                    PasswordHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("adm1n99"))),
                });
                db.Jobs.Add(new TalentMatch.Domain.Entities.Job
                {
                    Id = "job-legacy",
                    Title = "Legacy Role",
                    Department = "Engineering",
                    Organisation = "Northwind Analytics",
                    Status = "active",
                    CurrentConfigVersionId = "cfg-legacy",
                });
                db.JobConfigVersions.Add(new TalentMatch.Domain.Entities.JobConfigVersion
                {
                    Id = "cfg-legacy",
                    JobId = "job-legacy",
                    VersionNumber = 1,
                    RubricJson = """[{"name":"Technical Platform Skills","weight":0.6,"description":"Expert SQL experience; Expert Python experience"},{"name":"Collaboration and Communication","weight":0.4,"description":"Excellent written and verbal communication skills"}]""",
                    MustHavesJson = """[{"criterion":"Expert SQL experience","description":"Expert SQL experience"},{"criterion":"Expert Python experience","description":"Expert Python experience"},{"criterion":"Excellent written and verbal communication skills","description":"Excellent written and verbal communication skills"}]""",
                    DesiredCriteriaJson = "[]",
                    RubricSource = "manual",
                    RubricApprovalStatus = "draft"
                });
                db.SaveChanges();
            });
        }

        public HttpClient CreateAuthenticatedClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }
}
