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
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class OrganizationEndpointsTests :
    IClassFixture<OrganizationEndpointsTests.OrganizationFactory>
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string AdminObjectId = "22222222-2222-4222-8222-222222222222";
    private const string OrganizationAdminObjectId = "33333333-3333-4333-8333-333333333333";
    private const string TargetObjectId = "44444444-4444-4444-8444-444444444444";
    private const string OrganizationId = "55555555-5555-4555-8555-555555555555";
    private const string OtherOrganizationId = "66666666-6666-4666-8666-666666666666";
    private const string DepartmentId = "77777777-7777-4777-8777-777777777777";
    private const string ReplacementDepartmentId = "88888888-8888-4888-8888-888888888888";
    private const string OtherDepartmentId = "99999999-9999-4999-8999-999999999999";
    private const string RecruiterAssignmentId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string AnalyticsAssignmentId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    private readonly OrganizationFactory _factory;

    public OrganizationEndpointsTests(OrganizationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateOrganization_Admin_CreatesFirstDepartmentAtomicallyAndAudits()
    {
        await _factory.ResetAsync();
        var client = CreateClient("admin");

        var response = await client.PostAsJsonAsync("/api/organizations", new
        {
            name = "New Organization",
            initialDepartmentName = "Operations",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("departments").GetArrayLength().Should().Be(1);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = await db.Organizations
            .Include(item => item.Departments)
            .SingleAsync(item => item.Name == "New Organization");
        organization.Departments.Should().ContainSingle(item => item.Name == "Operations");
        await AssertAuditAsync(db, response, "auth.organization.changed", "succeeded", AdminObjectId);
    }

    [Fact]
    public async Task CreateOrganization_OrganizationAdmin_IsForbiddenAndAudited()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PostAsJsonAsync("/api/organizations", new
        {
            name = "Escaped Organization",
            initialDepartmentName = "Operations",
        });

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Organizations.AnyAsync(item => item.Name == "Escaped Organization")).Should().BeFalse();
        await AssertAuditAsync(db, response, "auth.scope.denied", "failed", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task CreateDepartment_OrganizationAdmin_InOwnOrganization_SucceedsAndAudits()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PostAsJsonAsync(
            $"/api/organizations/{OrganizationId}/departments",
            new { name = "Finance" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("organizationId").GetString().Should().Be(OrganizationId);
        payload.GetProperty("name").GetString().Should().Be("Finance");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Departments.AnyAsync(item =>
            item.OrganizationId == OrganizationId && item.Name == "Finance")).Should().BeTrue();
        await AssertAuditAsync(db, response, "auth.department.changed", "succeeded", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task CreateDepartment_OrganizationAdmin_InForeignOrganization_IsForbiddenWithoutMutation()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PostAsJsonAsync(
            $"/api/organizations/{OtherOrganizationId}/departments",
            new { name = "Unauthorized" });

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Departments.AnyAsync(item => item.Name == "Unauthorized")).Should().BeFalse();
        await AssertAuditAsync(db, response, "auth.scope.denied", "failed", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task UpdateDepartment_Rename_PreservesParentAndAudits()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PatchAsJsonAsync(
            $"/api/organizations/{OrganizationId}/departments/{ReplacementDepartmentId}",
            new { name = "People Operations" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("organizationId").GetString().Should().Be(OrganizationId);
        payload.GetProperty("name").GetString().Should().Be("People Operations");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var department = await db.Departments.SingleAsync(item => item.Id == ReplacementDepartmentId);
        department.OrganizationId.Should().Be(OrganizationId);
        department.Name.Should().Be("People Operations");
        await AssertAuditAsync(db, response, "auth.department.changed", "succeeded", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task UpdateDepartment_ReparentAttempt_ReturnsInvalidScopeAndLeavesParentUnchanged()
    {
        await _factory.ResetAsync();
        var client = CreateClient("admin");

        var response = await client.PatchAsJsonAsync(
            $"/api/organizations/{OrganizationId}/departments/{ReplacementDepartmentId}",
            new { name = "Moved", organizationId = OtherOrganizationId });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var department = await db.Departments.SingleAsync(item => item.Id == ReplacementDepartmentId);
        department.OrganizationId.Should().Be(OrganizationId);
        department.Name.Should().Be("Operations");
        await AssertAuditAsync(db, response, "auth.department.changed", "failed", AdminObjectId);
    }

    [Fact]
    public async Task RetireDepartment_LastActiveDepartment_ReturnsConflictAndPreservesIntegrity()
    {
        await _factory.ResetAsync();
        var client = CreateClient("admin");

        var response = await client.PatchAsJsonAsync(
            $"/api/organizations/{OtherOrganizationId}/departments/{OtherDepartmentId}",
            new { status = "retired" });

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "conflict");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Departments.SingleAsync(item => item.Id == OtherDepartmentId)).Status.Should().Be("active");
        await AssertAuditAsync(db, response, "auth.department.changed", "failed", AdminObjectId);
    }

    [Fact]
    public async Task RetireDepartment_ReferencedByActiveDefault_ReturnsConflictWithoutPartialChange()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PatchAsJsonAsync(
            $"/api/organizations/{OrganizationId}/departments/{DepartmentId}",
            new { status = "retired" });

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "conflict");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Departments.SingleAsync(item => item.Id == DepartmentId)).Status.Should().Be("active");
        (await db.OrganizationMemberships.CountAsync(item =>
            item.DefaultDepartmentMembershipId != null && item.Status == "active")).Should().Be(3);
        await AssertAuditAsync(db, response, "auth.department.changed", "failed", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task RegisterMembership_ValidDepartmentsAndExplicitDefault_ConvergesAndAudits()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PostAsJsonAsync(
            $"/api/organizations/{OrganizationId}/memberships",
            new
            {
                userObjectId = TargetObjectId,
                departmentIds = new[] { DepartmentId, ReplacementDepartmentId },
                defaultDepartmentId = ReplacementDepartmentId,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("defaultDepartmentId").GetString().Should().Be(ReplacementDepartmentId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = await db.OrganizationMemberships
            .Include(item => item.DefaultDepartmentMembership)
            .SingleAsync(item => item.UserId == TargetObjectId && item.OrganizationId == OrganizationId);
        membership.DefaultDepartmentMembership!.DepartmentId.Should().Be(ReplacementDepartmentId);
        await AssertAuditAsync(db, response, "auth.membership.activated", "succeeded", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task RegisterMembership_ForeignDefault_ReturnsInvalidScopeAndPreservesExistingDefault()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PostAsJsonAsync(
            $"/api/organizations/{OrganizationId}/memberships",
            new
            {
                userObjectId = TargetObjectId,
                departmentIds = new[] { DepartmentId },
                defaultDepartmentId = OtherDepartmentId,
            });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = await db.OrganizationMemberships
            .Include(item => item.DefaultDepartmentMembership)
            .SingleAsync(item => item.UserId == TargetObjectId && item.OrganizationId == OrganizationId);
        membership.DefaultDepartmentMembership!.DepartmentId.Should().Be(DepartmentId);
        await AssertAuditAsync(db, response, "auth.membership.activated", "failed", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task GrantRole_OrganizationAdmin_InOwnScope_CreatesDelegatedAssignmentAndAudits()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PostAsJsonAsync(
            $"/api/organizations/{OrganizationId}/role-assignments",
            new
            {
                userObjectId = TargetObjectId,
                role = "business_panel",
                departmentId = ReplacementDepartmentId,
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.RoleAssignments.AnyAsync(item =>
            item.UserId == TargetObjectId &&
            item.Role == "business_panel" &&
            item.OrganizationId == OrganizationId &&
            item.DepartmentId == ReplacementDepartmentId &&
            item.Source == "delegated" &&
            item.Status == "active")).Should().BeTrue();
        await AssertAuditAsync(db, response, "auth.assignment.activated", "succeeded", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task GrantRole_OrganizationAdmin_GlobalAdminEscalation_ReturnsInvalidScopeAndAuditsDenial()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.PostAsJsonAsync(
            $"/api/organizations/{OrganizationId}/role-assignments",
            new
            {
                userObjectId = TargetObjectId,
                role = "admin",
                departmentId = (string?)null,
            });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.RoleAssignments.AnyAsync(item =>
            item.UserId == TargetObjectId && item.Role == "admin")).Should().BeFalse();
        await AssertAuditAsync(db, response, "auth.scope.denied", "failed", OrganizationAdminObjectId);
    }

    [Fact]
    public async Task RevokeRole_TargetedAssignment_PreservesUnrelatedScopeAndAudits()
    {
        await _factory.ResetAsync();
        var client = CreateClient("organization-admin");

        var response = await client.DeleteAsync(
            $"/api/organizations/{OrganizationId}/role-assignments/{RecruiterAssignmentId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.RoleAssignments.SingleAsync(item => item.Id == RecruiterAssignmentId)).Status.Should().Be("revoked");
        (await db.RoleAssignments.SingleAsync(item => item.Id == AnalyticsAssignmentId)).Status.Should().Be("active");
        await AssertAuditAsync(db, response, "auth.assignment.revoked", "succeeded", OrganizationAdminObjectId);
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

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        response.StatusCode.Should().Be(expectedStatus);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetString().Should().Be(expectedError);
        payload.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("correlationId").GetString().Should().Be(GetCorrelationId(response));
    }

    private static async Task AssertAuditAsync(
        AppDbContext db,
        HttpResponseMessage response,
        string eventType,
        string result,
        string actorObjectId)
    {
        var correlationId = GetCorrelationId(response);
        var audit = await db.ProcessingEvents.SingleAsync(item => item.CorrelationId == correlationId);
        audit.EventType.Should().Be(eventType);
        audit.Actor.Should().Be(actorObjectId);

        using var payload = JsonDocument.Parse(audit.PayloadJson);
        payload.RootElement.GetProperty("result").GetString().Should().Be(result);
        payload.RootElement.ToString().Should().NotContain("token").And.NotContain("credential");
    }

    private static string GetCorrelationId(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        var correlationId = values!.Single();
        Guid.TryParse(correlationId, out _).Should().BeTrue();
        return correlationId;
    }

    public sealed class OrganizationFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

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
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }

    public sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "TestOrganizationEndpoints";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var scenario = Request.Headers["X-Test-Auth"].ToString();
            if (string.IsNullOrWhiteSpace(scenario))
                return Task.FromResult(AuthenticateResult.NoResult());

            var objectId = scenario == "organization-admin" ? OrganizationAdminObjectId : AdminObjectId;
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

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, AuthenticationSchemeName)));
        }
    }

    private static async Task SeedAsync(AppDbContext db)
    {
        var organizations = new[]
        {
            new Organization { Id = OrganizationId, Name = "Primary Organization", UpdatedBy = "test" },
            new Organization { Id = OtherOrganizationId, Name = "Other Organization", UpdatedBy = "test" },
        };
        var departments = new[]
        {
            new Department { Id = DepartmentId, OrganizationId = OrganizationId, Name = "Engineering", UpdatedBy = "test" },
            new Department { Id = ReplacementDepartmentId, OrganizationId = OrganizationId, Name = "Operations", UpdatedBy = "test" },
            new Department { Id = OtherDepartmentId, OrganizationId = OtherOrganizationId, Name = "Finance", UpdatedBy = "test" },
        };
        var users = new[]
        {
            CreateUser(AdminObjectId, "Application Admin"),
            CreateUser(OrganizationAdminObjectId, "Organization Admin"),
            CreateUser(TargetObjectId, "Target User"),
        };
        db.AddRange(organizations);
        db.AddRange(departments);
        db.AddRange(users);
        await db.SaveChangesAsync();

        var adminDepartmentMembership = CreateDepartmentMembership(AdminObjectId, DepartmentId);
        var organizationAdminDepartmentMembership = CreateDepartmentMembership(OrganizationAdminObjectId, DepartmentId);
        var targetDepartmentMembership = CreateDepartmentMembership(TargetObjectId, DepartmentId);
        var targetReplacementMembership = CreateDepartmentMembership(TargetObjectId, ReplacementDepartmentId);
        db.AddRange(
            adminDepartmentMembership,
            organizationAdminDepartmentMembership,
            targetDepartmentMembership,
            targetReplacementMembership);
        await db.SaveChangesAsync();

        db.OrganizationMemberships.AddRange(
            CreateOrganizationMembership(AdminObjectId, adminDepartmentMembership.Id),
            CreateOrganizationMembership(OrganizationAdminObjectId, organizationAdminDepartmentMembership.Id),
            CreateOrganizationMembership(TargetObjectId, targetDepartmentMembership.Id));
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
                UserId = OrganizationAdminObjectId,
                TenantId = TenantId,
                UserObjectId = OrganizationAdminObjectId,
                Role = "organization_admin",
                OrganizationId = OrganizationId,
                Source = "delegated",
                UpdatedBy = "test",
            },
            new RoleAssignment
            {
                Id = RecruiterAssignmentId,
                UserId = TargetObjectId,
                TenantId = TenantId,
                UserObjectId = TargetObjectId,
                Role = "recruiter",
                OrganizationId = OrganizationId,
                DepartmentId = DepartmentId,
                Source = "delegated",
                UpdatedBy = "test",
            },
            new RoleAssignment
            {
                Id = AnalyticsAssignmentId,
                UserId = TargetObjectId,
                TenantId = TenantId,
                UserObjectId = TargetObjectId,
                Role = "business_panel",
                OrganizationId = OrganizationId,
                DepartmentId = ReplacementDepartmentId,
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

    private static DepartmentMembership CreateDepartmentMembership(string userId, string departmentId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserId = userId,
        OrganizationId = OrganizationId,
        DepartmentId = departmentId,
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
}