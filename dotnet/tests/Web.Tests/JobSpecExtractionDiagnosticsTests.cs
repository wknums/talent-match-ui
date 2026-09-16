using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class JobSpecExtractionDiagnosticsTests
{
    [Fact]
    public async Task ConfigEndpoint_ExposesExtractionDiagnosticsSummary()
    {
        using var factory = new DiagnosticsFactory();
        using var client = factory.CreateAuthenticatedClient();
        await LoginAsAdmin(client);

        var response = await client.GetAsync("/api/jobs/job-1/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("extraction").GetProperty("instructionVersionId").GetString().Should().Be("instruction-v1");
        payload.GetProperty("extraction").GetProperty("validationFindings").EnumerateArray()
            .Select(item => item.GetProperty("code").GetString())
            .Should().Contain("needs_review");
    }

    [Fact]
    public async Task UpdateConfig_WithPreviouslyLinkedExtraction_CreatesEditedVersionWithoutRelinking()
    {
        using var factory = new DiagnosticsFactory();
        using var client = factory.CreateAuthenticatedClient();
        await LoginAsAdmin(client);

        var response = await client.PutAsJsonAsync("/api/jobs/job-1/config", new
        {
            RubricJson = """{"schemaVersion":"rubric-v2","categories":[{"id":"cat-1","name":"Technical Skills","weight":1,"description":"desc","order":0}],"items":[]}""",
            MustHavesJson = "[]",
            DesiredCriteriaJson = "[]",
            ScoringRunCount = 3,
            AggregationStrategy = "median",
            LonglistThreshold = 70,
            ShortlistThreshold = 85,
            VarianceThreshold = 15,
            RubricSource = "extracted",
            RawExtractionResponse = "{}",
            ExtractionId = "extract-1",
            ExtractionInstructionVersionId = "instruction-v1",
            ExpectedConfigVersionId = "cfg-1",
            RubricApprovalStatus = "draft"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var editedConfigId = payload.GetProperty("id").GetString();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Jobs.SingleAsync(job => job.Id == "job-1"))
            .CurrentConfigVersionId.Should().Be(editedConfigId);
        (await db.JobSpecExtractions.SingleAsync(extraction => extraction.Id == "extract-1"))
            .JobConfigVersionId.Should().Be("cfg-1");
    }

    private static async Task LoginAsAdmin(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "adm1n99" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class DiagnosticsFactory : WebApplicationFactory<Program>
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
                    Id = "job-1",
                    Title = "Data Engineer",
                    Department = "Engineering",
                    Organisation = "Northwind",
                    Status = "active",
                    CurrentConfigVersionId = "cfg-1"
                });
                db.JobConfigVersions.Add(new TalentMatch.Domain.Entities.JobConfigVersion
                {
                    Id = "cfg-1",
                    JobId = "job-1",
                    VersionNumber = 1,
                    RubricJson = """{"schemaVersion":"rubric-v2","categories":[{"id":"cat-1","name":"Technical Skills","weight":1,"description":"desc","order":0}],"items":[{"id":"item-1","categoryId":"cat-1","text":"Expert SQL experience","requirementType":"must_have","order":0,"sourceText":"Expert SQL experience","sourceLocation":"Required Qualifications","sourceRequirementId":"req-1","reviewStatus":"needs_review","createdFrom":"extracted"}]}""",
                    MustHavesJson = """[{"criterion":"Expert SQL experience","description":"Expert SQL experience"}]""",
                    DesiredCriteriaJson = "[]",
                    ExtractionId = "extract-1",
                    ExtractionInstructionVersionId = "instruction-v1"
                });
                db.ExtractionInstructionVersions.Add(new TalentMatch.Domain.Entities.ExtractionInstructionVersion
                {
                    Id = "instruction-v1",
                    VersionNumber = 1,
                    InstructionText = "Extract requirements",
                    ProtectedContractVersion = "extraction-rubric-v1",
                    Status = "active",
                    ValidationStatus = "valid",
                    ValidationFindingsJson = "[]",
                    CreatedBy = "seed"
                });
                db.JobSpecExtractions.Add(new TalentMatch.Domain.Entities.JobSpecExtraction
                {
                    Id = "extract-1",
                    JobId = "job-1",
                    JobConfigVersionId = "cfg-1",
                    Purpose = "job_creation",
                    InstructionVersionId = "instruction-v1",
                    ProtectedContractVersion = "extraction-rubric-v1",
                    SourceFileName = "sample-spec.md",
                    SourceMimeType = "text/markdown",
                    SourceSha256 = "hash",
                    RawResponse = "{}",
                    NormalizedResponseJson = "{}",
                    ValidationStatus = "valid",
                    ValidationFindingsJson = """[{"code":"needs_review","severity":"warning","path":"$.requirements[0]","message":"Review category mapping"}]""",
                    CreatedBy = "admin",
                    CompletedAt = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc),
                    CorrelationId = Guid.NewGuid().ToString()
                });
                db.SaveChanges();
            });
        }

        public HttpClient CreateAuthenticatedClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }
}
