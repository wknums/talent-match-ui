using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public class EntraAuthEndpointsTests : IClassFixture<EntraAuthEndpointsTests.EntraAuthFactory>
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string AssignedObjectId = "22222222-2222-2222-2222-222222222222";
    private const string UnassignedObjectId = "77777777-7777-7777-7777-777777777777";
    private const string DisabledObjectId = "88888888-8888-8888-8888-888888888888";
    private const string OrganizationId = "33333333-3333-3333-3333-333333333333";
    private const string DepartmentId = "44444444-4444-4444-4444-444444444444";

    private readonly EntraAuthFactory _factory;

    public EntraAuthEndpointsTests(EntraAuthFactory factory) => _factory = factory;

    [Fact]
    public async Task Me_AssignedIdentity_ReturnsSharedAuthorizationContextAndCorrelationId()
    {
        var client = CreateClient("assigned");

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Correlation-ID", out var correlationValues).Should().BeTrue();
        Guid.TryParse(correlationValues!.Single(), out _).Should().BeTrue();
        var payload = await response.Content.ReadFromJsonAsync<AuthorizationContextPayload>();
        payload.Should().NotBeNull();
        payload!.UserId.Should().Be(AssignedObjectId);
        payload.TenantId.Should().Be(TenantId);
        payload.ObjectId.Should().Be(AssignedObjectId);
        payload.GlobalRole.Should().BeNull();
        payload.Memberships.Should().ContainSingle();
        payload.Memberships[0].Departments.Should().ContainSingle();
        payload.Authorizations.Should().ContainSingle(authorization =>
            authorization.Role == "recruiter"
            && authorization.AssignmentSource == "delegated"
            && authorization.OrganizationId == OrganizationId
            && authorization.DepartmentId == DepartmentId);
    }

    [Theory]
    [InlineData("unassigned", HttpStatusCode.Forbidden, "assignment_missing")]
    [InlineData("disabled", HttpStatusCode.Forbidden, "identity_disabled")]
    [InlineData("wrong-tenant", HttpStatusCode.Unauthorized, "wrong_tenant")]
    [InlineData("stale", HttpStatusCode.Unauthorized, "token_stale")]
    public async Task Me_DeniedIdentity_ReturnsCanonicalSafeError(
        string scenario,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        var client = CreateClient(scenario);

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(expectedStatus);
        var payload = await response.Content.ReadFromJsonAsync<AuthErrorPayload>();
        payload.Should().NotBeNull();
        payload!.Error.Should().Be(expectedError);
        payload.Message.Should().NotBeNullOrWhiteSpace();
        payload.Message.Should().NotContain("Bearer ");
        Guid.TryParse(payload.CorrelationId, out _).Should().BeTrue();
        response.Headers.GetValues("X-Correlation-ID").Single().Should().Be(payload.CorrelationId);
    }

    [Fact]
    public async Task Me_WithoutBearerIdentity_ReturnsAuthRequired()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_RecordsSafeAuditEventAndReturnsNoContent()
    {
        var client = CreateClient("assigned");

        var response = await client.PostAsync("/api/auth/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        var auditEvent = await _factory.GetLatestEventAsync(ProcessingEvent.AuthorizationActions.Logout);
        auditEvent.Should().NotBeNull();
        auditEvent!.Actor.Should().Be(AssignedObjectId);
        auditEvent.CorrelationId.Should().Be(values!.Single());
        auditEvent.PayloadJson.ToLowerInvariant().Should().NotContain("token");
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/change-password")]
    [InlineData("/api/auth/request-password-reset")]
    public async Task PasswordEndpoint_InEntraMode_IsNotMapped(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(path, new { });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task ClientConfig_InEntraMode_ReturnsOnlyPublicMsalValues()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("authMode").GetString().Should().Be("entra");
        payload.GetProperty("tenantId").GetString().Should().Be(TenantId);
        payload.GetProperty("clientId").GetString().Should().Be("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        payload.GetProperty("apiScope").GetString().Should().Be("api://tenant.example/talent-api/access_as_user");
        payload.ToString().Should().NotContainEquivalentOf("secret");
    }

    [Fact]
    public async Task Startup_InEntraMode_DoesNotSeedPasswordAdmin()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var passwordAdmin = await db.Users.SingleOrDefaultAsync(user => user.Username == "admin");

        passwordAdmin.Should().BeNull();
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

    public sealed class EntraAuthFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["APP_AUTH_MODE"] = "entra",
                    ["AZURE_TENANT_ID"] = TenantId,
                    ["ENTRA_API_APP_CLIENT_ID"] = "99999999-9999-9999-9999-999999999999",
                    ["ENTRA_API_IDENTIFIER_URI"] = "api://tenant.example/talent-api",
                    ["ENTRA_API_SCOPE"] = "access_as_user",
                    ["ENTRA_STACK_A_CLIENT_ID"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    ["ENTRA_STACK_B_CLIENT_ID"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                });
            });
            builder.ConfigureServices(services =>
            {
                var dbOptions = services.SingleOrDefault(
                    descriptor => descriptor.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbOptions is not null)
                    services.Remove(dbOptions);

                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationSchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.AuthenticationSchemeName;
                        options.DefaultForbidScheme = TestAuthHandler.AuthenticationSchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationSchemeName, _ => { });
                services.AddAuthorization(options =>
                    options.AddPolicy("AccessAsUser", policy => policy.RequireAuthenticatedUser()));

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                Seed(db);
            });
        }

        public async Task<ProcessingEvent?> GetLatestEventAsync(string action)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.ProcessingEvents
                .AsNoTracking()
                .Where(item => item.EventType == action)
                .OrderByDescending(item => item.Timestamp)
                .FirstOrDefaultAsync();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }

        private static void Seed(AppDbContext db)
        {
            var organization = new Organization
            {
                Id = OrganizationId,
                Name = "Analytical Engines",
                UpdatedBy = "test",
            };
            var department = new Department
            {
                Id = DepartmentId,
                OrganizationId = OrganizationId,
                Name = "Engineering",
                UpdatedBy = "test",
            };
            var assigned = CreateUser(AssignedObjectId, true);
            var disabled = CreateUser(DisabledObjectId, false);
            var unassigned = CreateUser(UnassignedObjectId, true);

            db.AddRange(organization, department, assigned, disabled, unassigned);
            db.AddRange(
                CreateOrganizationMembership(AssignedObjectId),
                CreateDepartmentMembership(AssignedObjectId),
                CreateOrganizationMembership(DisabledObjectId),
                CreateDepartmentMembership(DisabledObjectId));
            db.RoleAssignments.AddRange(
                CreateAssignment(AssignedObjectId),
                CreateAssignment(DisabledObjectId));
            db.SaveChanges();
        }

        private static User CreateUser(string objectId, bool isActive)
            => new()
            {
                Id = objectId,
                AuthenticationProvider = "entra",
                EntraTenantId = TenantId,
                EntraObjectId = objectId,
                Username = $"{objectId[..8]}@example.com",
                FullName = "Test User",
                Email = $"{objectId[..8]}@example.com",
                PasswordHash = null,
                IsActive = isActive,
            };

        private static OrganizationMembership CreateOrganizationMembership(string userId)
            => new()
            {
                UserId = userId,
                OrganizationId = OrganizationId,
                UpdatedBy = "test",
            };

        private static DepartmentMembership CreateDepartmentMembership(string userId)
            => new()
            {
                UserId = userId,
                OrganizationId = OrganizationId,
                DepartmentId = DepartmentId,
                UpdatedBy = "test",
            };

        private static RoleAssignment CreateAssignment(string userId)
            => new()
            {
                UserId = userId,
                TenantId = TenantId,
                UserObjectId = userId,
                Role = "recruiter",
                OrganizationId = OrganizationId,
                DepartmentId = DepartmentId,
                Source = "delegated",
                UpdatedBy = "test",
            };
    }

    public sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "TestEntra";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var scenario = Request.Headers["X-Test-Auth"].ToString();
            if (string.IsNullOrWhiteSpace(scenario))
                return Task.FromResult(AuthenticateResult.NoResult());

            var objectId = scenario switch
            {
                "unassigned" => UnassignedObjectId,
                "disabled" => DisabledObjectId,
                _ => AssignedObjectId,
            };
            var tenantId = scenario == "wrong-tenant"
                ? "cccccccc-cccc-cccc-cccc-cccccccccccc"
                : TenantId;
            var issuedAt = scenario == "stale"
                ? DateTimeOffset.UtcNow.AddMinutes(-16)
                : DateTimeOffset.UtcNow.AddMinutes(-1);
            Claim[] claims =
            [
                new("tid", tenantId),
                new("oid", objectId),
                new("iat", issuedAt.ToUnixTimeSeconds().ToString()),
                new("scp", "access_as_user"),
                new("azp", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                new("preferred_username", "test@example.com"),
            ];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationSchemeName)));
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.ContentType = "application/json";
            var correlationId = Guid.NewGuid().ToString();
            Response.Headers["X-Correlation-ID"] = correlationId;
            await Response.WriteAsJsonAsync(new
            {
                error = "auth_required",
                message = "Sign in is required to access this application.",
                correlationId,
            });
        }
    }

    public sealed record AuthorizationContextPayload(
        string UserId,
        string TenantId,
        string ObjectId,
        string Username,
        string FullName,
        string? Email,
        string? GlobalRole,
        List<OrganizationMembershipPayload> Memberships,
        List<ScopedAuthorizationPayload> Authorizations,
        DateTimeOffset TokenIssuedAt,
        DateTimeOffset RefreshRequiredAt);

    public sealed record OrganizationMembershipPayload(
        string OrganizationId,
        string OrganizationName,
        List<DepartmentMembershipPayload> Departments);

    public sealed record DepartmentMembershipPayload(string DepartmentId, string DepartmentName);

    public sealed record ScopedAuthorizationPayload(
        string Role,
        string RoleLabel,
        string? OrganizationId,
        string? DepartmentId,
        string AssignmentSource);

    public sealed record AuthErrorPayload(string Error, string Message, string CorrelationId);
}