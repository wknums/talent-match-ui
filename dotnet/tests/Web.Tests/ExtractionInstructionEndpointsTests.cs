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
using TalentMatch.Application.JobExtraction.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class ExtractionInstructionEndpointsTests
{
    [Fact]
    public async Task AdminCanCreateValidateAndActivateInstructionVersion()
    {
        using var factory = new ExtractionInstructionFactory(TestFixture("valid-itemized-extraction.json"));
        using var client = factory.CreateAuthenticatedClient();
        await LoginAsAdmin(client);

        var create = await client.PostAsJsonAsync("/api/admin/extraction-instructions", new
        {
            instructionText = "Extract every independently assessable requirement as its own item.",
            changeNote = "Improve item splitting"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var versionId = created.GetProperty("id").GetString();

        var validate = await client.PostAsJsonAsync($"/api/admin/extraction-instructions/{versionId}/validate", new
        {
            fileName = "sample-spec.md",
            content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("# sample")),
            mimeType = "text/markdown"
        });
        validate.StatusCode.Should().Be(HttpStatusCode.OK);

        var activate = await client.PostAsJsonAsync($"/api/admin/extraction-instructions/{versionId}/activate", new
        {
            expectedConcurrencyVersion = created.GetProperty("concurrencyVersion").GetInt32() + 1
        });
        activate.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ProcessingEvents.Select(evt => evt.EventType).Should().Contain([
            "extraction-instruction.created",
            "extraction-instruction.validated",
            "extraction-instruction.activated"
        ]);
        db.ProcessingEvents.Should().OnlyContain(evt => !evt.PayloadJson.Contains("instructionText", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonAdminCannotMutateInstructions()
    {
        using var factory = new ExtractionInstructionFactory(TestFixture("valid-itemized-extraction.json"));
        using var client = factory.CreateAuthenticatedClient();
        await LoginAsRecruiter(client);

        var create = await client.PostAsJsonAsync("/api/admin/extraction-instructions", new
        {
            instructionText = "Draft",
            changeNote = "Blocked"
        });

        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("forbidden");
        body.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        create.Headers.GetValues("X-Correlation-ID").Single().Should().Be(body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ActivationConflict_ReturnsCorrelatedStaleVersion()
    {
        using var factory = new ExtractionInstructionFactory(TestFixture("valid-itemized-extraction.json"));
        using var client = factory.CreateAuthenticatedClient();
        await LoginAsAdmin(client);

        var response = await client.PostAsJsonAsync("/api/admin/extraction-instructions/instruction-v1/activate", new
        {
            expectedConcurrencyVersion = 99
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("stale_version");
        body.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        response.Headers.GetValues("X-Correlation-ID").Single().Should().Be(body.GetProperty("correlationId").GetString());
    }

    private static string TestFixture(string fileName)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "specs", "001-dynamic-rubric-editor", "contracts", "fixtures", fileName));

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                    && Directory.Exists(Path.Combine(directory.FullName, "dotnet")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static async Task LoginAsAdmin(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "adm1n99" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task LoginAsRecruiter(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = "recruiter", Password = "test123" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class ExtractionInstructionFactory : WebApplicationFactory<Program>
    {
        private readonly string _rawResponse;
        private SqliteConnection? _connection;

        public ExtractionInstructionFactory(string rawResponse)
        {
            _rawResponse = rawResponse;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IJobSpecExtractionTransport>();
                services.RemoveAll<IReasoningModelCatalog>();

                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                services.AddSingleton<IJobSpecExtractionTransport>(new StubTransport(_rawResponse));
                services.AddSingleton<IReasoningModelCatalog>(new StubReasoningModelCatalog());

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                db.Users.AddRange(
                    new TalentMatch.Domain.Entities.User
                    {
                        Username = "admin",
                        Role = "admin",
                        FullName = "Administrator",
                        Department = "all",
                        PasswordHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("adm1n99"))),
                    },
                    new TalentMatch.Domain.Entities.User
                    {
                        Username = "recruiter",
                        Role = "recruiter",
                        FullName = "Recruiter",
                        Department = "Engineering",
                        PasswordHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test123"))),
                    });
                db.ExtractionInstructionVersions.Add(new TalentMatch.Domain.Entities.ExtractionInstructionVersion
                {
                    Id = "instruction-v1",
                    VersionNumber = 1,
                    InstructionText = "Extract every requirement as an item.",
                    ModelId = "o3",
                    ReasoningLevel = "high",
                    ProtectedContractVersion = "extraction-rubric-v1",
                    Status = "active",
                    ValidationStatus = "valid",
                    ValidationFindingsJson = "[]",
                    CreatedBy = "seed",
                    ActivatedBy = "seed",
                    ActivatedAt = DateTime.UtcNow,
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

    private sealed class StubTransport : IJobSpecExtractionTransport
    {
        private readonly string _rawResponse;
        public StubTransport(string rawResponse) => _rawResponse = rawResponse;

        public Task<string> ExtractAsync(byte[] documentBytes, string fileName, string mimeType, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(_rawResponse);
    }

    private sealed class StubReasoningModelCatalog : IReasoningModelCatalog
    {
        public Task<ReasoningModelsResponse> GetAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReasoningModelsResponse(
                "o3",
                "high",
                ["low", "medium", "high"],
                [new ReasoningModelOption("reason01", "o3", true)]));

        public Task<ScoringProfile> ResolveForExecutionAsync(
            string? modelId,
            string? reasoningEffort,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScoringProfile(
                string.IsNullOrWhiteSpace(modelId) ? "o3" : modelId,
                string.IsNullOrWhiteSpace(reasoningEffort) ? "high" : reasoningEffort));

        public Task<ScoringProfile> ValidateAsync(
            string modelId,
            string reasoningEffort,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScoringProfile(modelId, reasoningEffort));
    }
}
