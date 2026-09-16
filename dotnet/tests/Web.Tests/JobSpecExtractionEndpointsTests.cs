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
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class JobSpecExtractionEndpointsTests
{
    [Fact]
    public async Task ExtractSpec_ReturnsRubricV2_AndPersistsExtractionRecord()
    {
        using var factory = new ExtractionFactory(TestFixture("valid-itemized-extraction.json"));
        using var client = factory.CreateAuthenticatedClient();

        await LoginAsAdmin(client);

        var response = await client.PostAsJsonAsync("/api/jobs/extract-spec", new
        {
            FileName = "sample-spec.md",
            Content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("# sample")),
            MimeType = "text/markdown",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("validationStatus").GetString().Should().Be("valid");
        payload.GetProperty("title").GetString().Should().Be("Senior Data Platform Engineer");
        payload.GetProperty("rubric").GetProperty("schemaVersion").GetString().Should().Be("rubric-v2");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var extraction = await db.JobSpecExtractions.SingleAsync();
        extraction.ValidationStatus.Should().Be("valid");
        extraction.ProtectedContractVersion.Should().Be("extraction-rubric-v1");
        extraction.SourceFileName.Should().Be("sample-spec.md");
    }

    [Fact]
    public async Task ExtractSpec_ReturnsUnprocessableEntity_ForInvalidContractOutput()
    {
        using var factory = new ExtractionFactory(TestFixture("invalid-weight-total.json"));
        using var client = factory.CreateAuthenticatedClient();

        await LoginAsAdmin(client);

        var response = await client.PostAsJsonAsync("/api/jobs/extract-spec", new
        {
            FileName = "sample-spec.md",
            Content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("# sample")),
            MimeType = "text/markdown",
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetString().Should().Be("validation_failed");
        payload.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        response.Headers.GetValues("X-Correlation-ID").Single().Should().Be(payload.GetProperty("correlationId").GetString());
        payload.GetProperty("validationStatus").GetString().Should().Be("invalid");
        payload.GetProperty("validationFindings")
            .EnumerateArray()
            .Select(item => item.GetProperty("code").GetString())
            .Should()
            .Contain("invalid_weight_total");
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static async Task LoginAsAdmin(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "adm1n99" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class ExtractionFactory : WebApplicationFactory<Program>
    {
        private readonly string _rawResponse;
        private SqliteConnection? _connection;

        public ExtractionFactory(string rawResponse)
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

                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                services.AddSingleton<IJobSpecExtractionTransport>(new StubTransport(_rawResponse));

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                db.Users.Add(new User
                {
                    Username = "admin",
                    Role = "admin",
                    FullName = "Administrator",
                    Department = "all",
                    PasswordHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("adm1n99"))),
                });
                db.ExtractionInstructionVersions.Add(new ExtractionInstructionVersion
                {
                    Id = "instruction-v1",
                    VersionNumber = 1,
                    InstructionText = "Extract every requirement as an item.",
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
            HandleCookies = true,
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

        public StubTransport(string rawResponse)
        {
            _rawResponse = rawResponse;
        }

        public Task<string> ExtractAsync(
            byte[] documentBytes,
            string fileName,
            string mimeType,
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_rawResponse);
    }
}
