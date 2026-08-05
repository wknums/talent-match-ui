using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Web.Tests;

public sealed class NavigationAuditEndpointsTests :
    IClassFixture<NavigationAuditEndpointsTests.NavigationAuditFactory>
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string AdminObjectId = "22222222-2222-4222-8222-222222222222";
    private const string RecruiterObjectId = "33333333-3333-4333-8333-333333333333";
    private const string SpoofedObjectId = "99999999-9999-4999-8999-999999999999";
    private const string OrganizationId = "44444444-4444-4444-8444-444444444444";
    private const string DepartmentId = "55555555-5555-4555-8555-555555555555";

    private readonly NavigationAuditFactory _factory;

    public NavigationAuditEndpointsTests(NavigationAuditFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("collapse")]
    [InlineData("expand")]
    public async Task RecordNavigation_WritesExactlyOneAuditWithTheClientCorrelationId(string action)
    {
        await _factory.ResetAsync();
        var client = CreateClient("recruiter");
        var correlationId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/navigation/audit", new
        {
            action,
            correlationId,
            requestedAt = DateTimeOffset.UtcNow,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        GetCorrelationId(response).Should().Be(correlationId.ToString());
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("correlationId").GetString().Should().Be(correlationId.ToString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = (await NavigationAuditsAsync(db))
            .Should().ContainSingle().Subject;
        audit.CorrelationId.Should().Be(correlationId.ToString());
        audit.EventType.Should().Be(ProcessingEvent.AuthorizationActions.NavigationShellChanged);
        audit.EntityType.Should().Be("navigation_shell");
        using var details = JsonDocument.Parse(audit.PayloadJson);
        details.RootElement.GetProperty("action").GetString().Should().Be(action);
        details.RootElement.GetProperty("result").GetString().Should().Be("succeeded");
    }

    [Fact]
    public async Task RecordNavigation_DerivesActorFromTheTokenAndIgnoresClientSuppliedIdentity()
    {
        await _factory.ResetAsync();
        var client = CreateClient("recruiter");
        var correlationId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/navigation/audit", new
        {
            action = "collapse",
            correlationId,
            requestedAt = DateTimeOffset.UtcNow,
            actor = SpoofedObjectId,
            objectId = SpoofedObjectId,
            tenantId = "00000000-0000-4000-8000-000000000000",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = (await NavigationAuditsAsync(db)).Should().ContainSingle().Subject;
        audit.Actor.Should().Be(RecruiterObjectId);
        audit.PayloadJson.Should().NotContain(SpoofedObjectId);
    }

    [Fact]
    public async Task RecordNavigation_ReplayOfTheSameCorrelationIdRemainsASingleImmutableAudit()
    {
        await _factory.ResetAsync();
        var client = CreateClient("admin");
        var correlationId = Guid.NewGuid();
        var request = new
        {
            action = "collapse",
            correlationId,
            requestedAt = DateTimeOffset.UtcNow,
        };

        (await client.PostAsJsonAsync("/api/navigation/audit", request))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        ProcessingEvent original;
        await using (var firstScope = _factory.Services.CreateAsyncScope())
        {
            original = (await NavigationAuditsAsync(
                firstScope.ServiceProvider.GetRequiredService<AppDbContext>()))
                .Should().ContainSingle().Subject;
        }

        var replay = await client.PostAsJsonAsync("/api/navigation/audit", new
        {
            action = "expand",
            correlationId,
            requestedAt = DateTimeOffset.UtcNow,
        });

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = (await NavigationAuditsAsync(db)).Should().ContainSingle().Subject;
        stored.Id.Should().Be(original.Id);
        stored.PayloadJson.Should().Be(original.PayloadJson);
        stored.Timestamp.Should().Be(original.Timestamp);
    }

    [Theory]
    [InlineData("toggle")]
    [InlineData("")]
    public async Task RecordNavigation_UnsupportedAction_IsRejectedWithoutAudit(string action)
    {
        await _factory.ResetAsync();
        var client = CreateClient("recruiter");

        var response = await client.PostAsJsonAsync("/api/navigation/audit", new
        {
            action,
            correlationId = Guid.NewGuid(),
            requestedAt = DateTimeOffset.UtcNow,
        });

        await AssertSafeErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
        await AssertNoAuditAsync();
    }

    [Fact]
    public async Task RecordNavigation_MalformedCorrelationId_IsRejectedWithoutAudit()
    {
        await _factory.ResetAsync();
        var client = CreateClient("recruiter");

        var response = await client.PostAsJsonAsync("/api/navigation/audit", new
        {
            action = "collapse",
            correlationId = "not-a-correlation-id",
            requestedAt = DateTimeOffset.UtcNow,
        });

        await AssertSafeErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
        await AssertNoAuditAsync();
    }

    [Fact]
    public async Task RecordNavigation_Unauthenticated_IsRejectedWithoutAuditOrLeakage()
    {
        await _factory.ResetAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsJsonAsync("/api/navigation/audit", new
        {
            action = "collapse",
            correlationId = Guid.NewGuid(),
            requestedAt = DateTimeOffset.UtcNow,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertNoSensitiveContentAsync(response);
        await AssertNoAuditAsync();
    }

    [Fact]
    public async Task RecordNavigation_AuditPersistenceFailure_ReportsRetryableUnavailability()
    {
        await _factory.ResetAsync();
        var client = CreateClient("recruiter");
        _factory.FailAudits = true;
        try
        {
            var response = await client.PostAsJsonAsync("/api/navigation/audit", new
            {
                action = "collapse",
                correlationId = Guid.NewGuid(),
                requestedAt = DateTimeOffset.UtcNow,
            });

            await AssertSafeErrorAsync(response, HttpStatusCode.ServiceUnavailable, "audit_unavailable");
        }
        finally
        {
            _factory.FailAudits = false;
        }

        await AssertNoAuditAsync();
    }

    private HttpClient CreateClient(string scenario)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Auth", scenario);
        return client;
    }

    private async Task AssertNoAuditAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await NavigationAuditsAsync(db)).Should().BeEmpty();
    }

    private static async Task<IReadOnlyList<ProcessingEvent>> NavigationAuditsAsync(AppDbContext db)
        => await db.ProcessingEvents
            .Where(item => item.EventType == ProcessingEvent.AuthorizationActions.NavigationShellChanged)
            .ToListAsync();

    private static async Task AssertSafeErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        response.StatusCode.Should().Be(expectedStatus);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetString().Should().Be(expectedError);
        payload.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("correlationId").GetString().Should().Be(GetCorrelationId(response));
        await AssertNoSensitiveContentAsync(response);
    }

    private static async Task AssertNoSensitiveContentAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.ToLowerInvariant()
            .Should().NotContain("token")
            .And.NotContain("credential")
            .And.NotContain("secret")
            .And.NotContain("stack trace")
            .And.NotContain("exception");
    }

    private static string GetCorrelationId(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        var correlationId = values!.Single();
        Guid.TryParse(correlationId, out _).Should().BeTrue();
        return correlationId;
    }

    public sealed class NavigationAuditFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

        public bool FailAudits { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("APP_AUTH_MODE", "entra");
            builder.UseSetting("AZURE_TENANT_ID", TenantId);
            builder.UseSetting("ENTRA_API_APP_CLIENT_ID", "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
            builder.UseSetting("ENTRA_API_IDENTIFIER_URI", "api://tenant.example/talent-api");
            builder.UseSetting("ENTRA_API_SCOPE", "access_as_user");
            builder.UseSetting("ENTRA_STACK_A_CLIENT_ID", "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
            builder.UseSetting("ENTRA_STACK_B_CLIENT_ID", "dddddddd-dddd-4ddd-8ddd-dddddddddddd");
            builder.UseSetting("ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID", AdminObjectId);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["APP_AUTH_MODE"] = "entra",
                    ["AZURE_TENANT_ID"] = TenantId,
                    ["ENTRA_API_APP_CLIENT_ID"] = "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    ["ENTRA_API_IDENTIFIER_URI"] = "api://tenant.example/talent-api",
                    ["ENTRA_API_SCOPE"] = "access_as_user",
                    ["ENTRA_STACK_A_CLIENT_ID"] = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                    ["ENTRA_STACK_B_CLIENT_ID"] = "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                    ["ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID"] = AdminObjectId,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                services.RemoveAll<INavigationAuditRepository>();
                services.AddScoped<NavigationAuditRepository>();
                services.AddScoped<INavigationAuditRepository>(provider =>
                    new FaultInjectingNavigationAuditRepository(
                        provider.GetRequiredService<NavigationAuditRepository>(),
                        () => FailAudits));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationSchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.AuthenticationSchemeName;
                        options.DefaultForbidScheme = TestAuthHandler.AuthenticationSchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.AuthenticationSchemeName,
                        _ => { });
                services.AddAuthorization(options =>
                    options.AddPolicy("AccessAsUser", policy => policy.RequireAuthenticatedUser()));
            });
        }

        public async Task ResetAsync()
        {
            FailAudits = false;
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
        }

        private static async Task SeedAsync(AppDbContext db)
        {
            db.Add(new Organization { Id = OrganizationId, Name = "Primary Organization", UpdatedBy = "test" });
            db.Add(new Department
            {
                Id = DepartmentId,
                OrganizationId = OrganizationId,
                Name = "Engineering",
                UpdatedBy = "test",
            });
            db.AddRange(
                CreateUser(AdminObjectId, "Application Admin"),
                CreateUser(RecruiterObjectId, "Recruiter User"));
            await db.SaveChangesAsync();

            var adminDepartmentMembership = CreateDepartmentMembership(AdminObjectId);
            var recruiterDepartmentMembership = CreateDepartmentMembership(RecruiterObjectId);
            db.AddRange(adminDepartmentMembership, recruiterDepartmentMembership);
            await db.SaveChangesAsync();

            db.OrganizationMemberships.AddRange(
                CreateOrganizationMembership(AdminObjectId, adminDepartmentMembership.Id),
                CreateOrganizationMembership(RecruiterObjectId, recruiterDepartmentMembership.Id));
            db.RoleAssignments.AddRange(
                new RoleAssignment
                {
                    UserId = AdminObjectId,
                    TenantId = TenantId,
                    UserObjectId = AdminObjectId,
                    Role = "admin",
                    Source = "bootstrap",
                    UpdatedBy = "test",
                },
                new RoleAssignment
                {
                    UserId = RecruiterObjectId,
                    TenantId = TenantId,
                    UserObjectId = RecruiterObjectId,
                    Role = "recruiter",
                    OrganizationId = OrganizationId,
                    DepartmentId = DepartmentId,
                    Source = "delegated",
                    UpdatedBy = "test",
                });
            await db.SaveChangesAsync();
        }

        private static User CreateUser(string objectId, string fullName) => new()
        {
            Id = objectId,
            AuthenticationProvider = "entra",
            EntraTenantId = TenantId,
            EntraObjectId = objectId,
            Username = $"{objectId[..8]}@example.com",
            FullName = fullName,
            Email = $"{objectId[..8]}@example.com",
            PasswordHash = null,
            IsActive = true,
        };

        private static DepartmentMembership CreateDepartmentMembership(string userId) => new()
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            OrganizationId = OrganizationId,
            DepartmentId = DepartmentId,
            UpdatedBy = "test",
        };

        private static OrganizationMembership CreateOrganizationMembership(
            string userId,
            string defaultDepartmentMembershipId) => new()
        {
            UserId = userId,
            OrganizationId = OrganizationId,
            DefaultDepartmentMembershipId = defaultDepartmentMembershipId,
            UpdatedBy = "test",
        };

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }

    private sealed class FaultInjectingNavigationAuditRepository(
        INavigationAuditRepository inner,
        Func<bool> shouldFail) : INavigationAuditRepository
    {
        public Task RecordAsync(
            NavigationAuditEntry entry,
            CancellationToken cancellationToken = default)
        {
            if (shouldFail())
                throw new InvalidOperationException("Audit storage is unavailable.");

            return inner.RecordAsync(entry, cancellationToken);
        }
    }

    public sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "TestNavigationAuditEndpoints";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var scenario = Request.Headers["X-Test-Auth"].ToString();
            if (string.IsNullOrWhiteSpace(scenario))
                return Task.FromResult(AuthenticateResult.NoResult());

            var objectId = scenario == "admin" ? AdminObjectId : RecruiterObjectId;
            var claims = new List<Claim>
            {
                new("tid", TenantId),
                new("oid", objectId),
                new("iat", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds().ToString()),
                new("scp", "access_as_user"),
                new("azp", "dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
                new("preferred_username", $"{scenario}@example.com"),
                new("name", scenario),
            };
            if (scenario == "admin")
                claims.Add(new Claim("roles", "admin"));
            else
                claims.Add(new Claim("roles", "recruiter"));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, AuthenticationSchemeName)));
        }
    }
}
