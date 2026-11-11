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

namespace TalentMatch.Web.Tests;

public sealed class EntraAccessManagementEndpointsTests :
    IClassFixture<EntraAccessManagementEndpointsTests.EntraFactory>,
    IClassFixture<EntraAccessManagementEndpointsTests.SimpleFactory>
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string AdminObjectId = "22222222-2222-4222-8222-222222222222";
    private const string OrganizationAdminObjectId = "33333333-3333-4333-8333-333333333333";
    private const string TargetObjectId = "44444444-4444-4444-8444-444444444444";
    private const string OrganizationId = "55555555-5555-4555-8555-555555555555";
    private const string OtherOrganizationId = "66666666-6666-4666-8666-666666666666";
    private const string DepartmentId = "77777777-7777-4777-8777-777777777777";
    private const string OtherDepartmentId = "88888888-8888-4888-8888-888888888888";
    private const string AssignmentId = "99999999-9999-4999-8999-999999999999";
    private const string SimpleAdminId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    private readonly EntraFactory _entraFactory;
    private readonly SimpleFactory _simpleFactory;

    public EntraAccessManagementEndpointsTests(EntraFactory entraFactory, SimpleFactory simpleFactory)
    {
        _entraFactory = entraFactory;
        _simpleFactory = simpleFactory;
    }

    [Fact]
    public async Task List_Admin_ReturnsContractPageAndServerDerivedActor()
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("admin");

        var response = await client.GetAsync(
            $"/api/access-management/users?search=target&organizationId={OrganizationId}&status=active&cursor=next&limit=10&actorObjectId={TargetObjectId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("items").GetArrayLength().Should().Be(1);
        payload.GetProperty("nextCursor").GetString().Should().Be("cursor-2");
        _entraFactory.Repository.LastActor.Should().NotBeNull();
        _entraFactory.Repository.LastActor!.ObjectId.Should().Be(AdminObjectId);
        _entraFactory.Repository.LastActor.GlobalAdmin.Should().BeTrue();
        _entraFactory.Repository.LastSearch.Should().Be(new EntraAccessSearch(
            "target", OrganizationId, "active", "next", 10));
        AssertCorrelation(response, _entraFactory.Repository.LastActor.CorrelationId);
    }

    [Fact]
    public async Task Detail_OrganizationAdmin_UsesOnlyServerResolvedOrganizationScope()
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("organization-admin");

        var response = await client.GetAsync($"/api/access-management/users/{TargetObjectId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("objectId").GetString().Should().Be(TargetObjectId);
        _entraFactory.Repository.LastActor.Should().NotBeNull();
        _entraFactory.Repository.LastActor!.ObjectId.Should().Be(OrganizationAdminObjectId);
        _entraFactory.Repository.LastActor.GlobalAdmin.Should().BeFalse();
        _entraFactory.Repository.LastActor.OrganizationAdminIds.Should().Equal(OrganizationId);
        AssertCorrelation(response, _entraFactory.Repository.LastActor.CorrelationId);
    }

    [Fact]
    public async Task Put_Admin_ConvergesOrganizationAccessAndReturnsUpdatedAggregate()
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("admin");

        var response = await client.PutAsJsonAsync(
            $"/api/access-management/users/{TargetObjectId}/organizations/{OrganizationId}",
            ValidPutRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("authorizationVersion").GetInt32().Should().Be(1);
        _entraFactory.Repository.LastPut.Should().NotBeNull();
        _entraFactory.Repository.LastPut!.ExpectedVersion.Should().Be(0);
        _entraFactory.Repository.LastPut.Membership.DefaultDepartmentId.Should().Be(DepartmentId);
        _entraFactory.Repository.LastPut.RoleAssignments.Should().ContainSingle(role =>
            role.Role == "recruiter" && role.DepartmentId == DepartmentId);
        AssertCorrelation(response, _entraFactory.Repository.LastActor!.CorrelationId);
    }

    [Fact]
    public async Task Patch_OrganizationAdmin_GlobalActivationChangeIsForbiddenWithSafeCorrelation()
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("organization-admin");

        var response = await client.PatchAsJsonAsync(
            $"/api/access-management/users/{TargetObjectId}",
            new { expectedVersion = 0, isActive = false });

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        _entraFactory.Repository.UpdateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Patch_Admin_UpdatesGlobalActivationAndReturnsUpdatedAggregate()
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("admin");

        var response = await client.PatchAsJsonAsync(
            $"/api/access-management/users/{TargetObjectId}",
            new { expectedVersion = 0, isActive = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("isActive").GetBoolean().Should().BeFalse();
        payload.GetProperty("authorizationVersion").GetInt32().Should().Be(1);
        _entraFactory.Repository.UpdateCalls.Should().Be(1);
        _entraFactory.Repository.LastActor!.ObjectId.Should().Be(AdminObjectId);
        AssertCorrelation(response, _entraFactory.Repository.LastActor.CorrelationId);
    }

    [Fact]
    public async Task Delete_OrganizationAdmin_RevokesDelegatedRoleInsideOwnScope()
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("organization-admin");

        var response = await client.DeleteAsync(
            $"/api/access-management/users/{TargetObjectId}/organizations/{OrganizationId}/role-assignments/{AssignmentId}?expectedVersion=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _entraFactory.Repository.LastRevocation.Should().Be(
            (TargetObjectId, OrganizationId, AssignmentId, 0));
        _entraFactory.Repository.LastActor!.OrganizationAdminIds.Should().Equal(OrganizationId);
        AssertCorrelation(response, _entraFactory.Repository.LastActor.CorrelationId);
    }

    [Theory]
    [InlineData("put")]
    [InlineData("delete")]
    public async Task OrganizationAdmin_ForeignOrganizationMutationIsForbidden(string operation)
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("organization-admin");

        var response = operation == "put"
            ? await client.PutAsJsonAsync(
                $"/api/access-management/users/{TargetObjectId}/organizations/{OtherOrganizationId}",
                ValidPutRequest())
            : await client.DeleteAsync(
                $"/api/access-management/users/{TargetObjectId}/organizations/{OtherOrganizationId}/role-assignments/{AssignmentId}?expectedVersion=0");

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        _entraFactory.Repository.PutCalls.Should().Be(0);
        _entraFactory.Repository.RevokeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Put_InvalidDesiredState_ReturnsCanonicalBadRequestWithoutCallingRepository()
    {
        _entraFactory.Repository.Reset();
        var client = CreateEntraClient("admin");
        var request = ValidPutRequest() with
        {
            Membership = new MembershipRequest("active", [DepartmentId], OtherDepartmentId),
        };

        var response = await client.PutAsJsonAsync(
            $"/api/access-management/users/{TargetObjectId}/organizations/{OrganizationId}",
            request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_scope");
        _entraFactory.Repository.PutCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("not_found", HttpStatusCode.NotFound)]
    [InlineData("version_conflict", HttpStatusCode.Conflict)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    public async Task Put_ApplicationFailure_ReturnsSafeCanonicalError(string code, HttpStatusCode statusCode)
    {
        _entraFactory.Repository.Reset();
        _entraFactory.Repository.NextException = new EntraAccessManagementException(
            code,
            "Bearer secret-token and internal database details must not escape.");
        var client = CreateEntraClient("admin");

        var response = await client.PutAsJsonAsync(
            $"/api/access-management/users/{TargetObjectId}/organizations/{OrganizationId}",
            ValidPutRequest());

        var payload = await AssertErrorAsync(response, statusCode, code);
        payload.GetProperty("message").GetString().Should().NotContain("Bearer");
        payload.ToString().Should().NotContain("secret-token");
    }

    [Theory]
    [InlineData(HttpMethodNames.Get, "/api/access-management/users")]
    [InlineData(HttpMethodNames.Get, "/api/access-management/users/44444444-4444-4444-8444-444444444444")]
    [InlineData(HttpMethodNames.Put, "/api/access-management/users/44444444-4444-4444-8444-444444444444/organizations/55555555-5555-4555-8555-555555555555")]
    [InlineData(HttpMethodNames.Patch, "/api/access-management/users/44444444-4444-4444-8444-444444444444")]
    [InlineData(HttpMethodNames.Delete, "/api/access-management/users/44444444-4444-4444-8444-444444444444/organizations/55555555-5555-4555-8555-555555555555/role-assignments/99999999-9999-4999-8999-999999999999?expectedVersion=0")]
    public async Task AccessManagementEndpoint_InSimpleMode_IsNotMapped(string method, string path)
    {
        var client = _simpleFactory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is HttpMethodNames.Put or HttpMethodNames.Patch)
            request.Content = JsonContent.Create(ValidPutRequest());

        var response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task LocalPasswordUsers_AreUnavailableInEntraModeAndPreservedInSimpleMode()
    {
        var entraClient = CreateEntraClient("admin");
        var simpleClient = _simpleFactory.CreateAuthenticatedClient();

        var entraResponse = await entraClient.GetAsync("/api/users");
        var simpleResponse = await simpleClient.GetAsync("/api/users");

        entraResponse.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        simpleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LocalPasswordUserWorkflow_InSimpleMode_RemainsFunctional()
    {
        var client = _simpleFactory.CreateAuthenticatedClient();
        var username = $"local-{Guid.NewGuid():N}";

        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username,
            role = "recruiter",
            department = "engineering",
            password = "password99",
            fullName = "Local Recruiter",
            email = $"{username}@example.com",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = created.GetProperty("id").GetString();
        userId.Should().NotBeNullOrWhiteSpace();

        var updateResponse = await client.PutAsJsonAsync($"/api/users/{userId}", new
        {
            fullName = "Updated Local Recruiter",
            email = $"updated-{username}@example.com",
            role = "recruiter",
            department = "engineering",
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resetPasswordResponse = await client.PostAsJsonAsync($"/api/users/{userId}/reset-password", new
        {
            newPassword = "new-password99",
        });
        resetPasswordResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var requestResetResponse = await client.PostAsJsonAsync("/api/users/reset-requests", new
        {
            reason = "Simple-mode regression check",
        });
        requestResetResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var resetRequest = await requestResetResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = resetRequest.GetProperty("id").GetString();

        (await client.GetAsync("/api/users/reset-requests")).StatusCode.Should().Be(HttpStatusCode.OK);
        var resolveResponse = await client.PutAsJsonAsync($"/api/users/reset-requests/{requestId}", new
        {
            action = "approved",
            newPassword = "admin-password99",
        });
        resolveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync($"/api/users/{userId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private HttpClient CreateEntraClient(string scenario)
    {
        var client = _entraFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Auth", scenario);
        return client;
    }

    private static PutRequest ValidPutRequest() => new(
        0,
        new ProfileRequest("target@example.com", "Target User", "target@example.com"),
        new MembershipRequest("active", [DepartmentId], DepartmentId),
        [new RoleRequest("recruiter", DepartmentId)]);

    private static void AssertCorrelation(HttpResponseMessage response, string expectedCorrelationId)
    {
        response.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        var correlationId = values!.Single();
        Guid.TryParse(correlationId, out _).Should().BeTrue();
        correlationId.Should().Be(expectedCorrelationId);
    }

    private static async Task<JsonElement> AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        response.StatusCode.Should().Be(expectedStatus);
        response.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetString().Should().Be(expectedError);
        payload.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        var correlationValues = values!;
        payload.GetProperty("correlationId").GetString().Should().Be(correlationValues.Single());
        Guid.TryParse(correlationValues.Single(), out _).Should().BeTrue();
        return payload;
    }

    public sealed class EntraFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

        public RecordingRepository Repository { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("APP_AUTH_MODE", "entra");
            builder.UseSetting("AZURE_TENANT_ID", TenantId);
            builder.UseSetting("ENTRA_API_APP_CLIENT_ID", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
            builder.UseSetting("ENTRA_API_IDENTIFIER_URI", "api://tenant.example/talent-api");
            builder.UseSetting("ENTRA_API_SCOPE", "access_as_user");
            builder.UseSetting("ENTRA_STACK_A_CLIENT_ID", "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
            builder.UseSetting("ENTRA_STACK_B_CLIENT_ID", "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
            builder.UseSetting("ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID", AdminObjectId);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["APP_AUTH_MODE"] = "entra",
                    ["AZURE_TENANT_ID"] = TenantId,
                    ["ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID"] = AdminObjectId,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IEntraAccessManagementRepository>();

                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                services.AddSingleton(Repository);
                services.AddSingleton<IEntraAccessManagementRepository>(Repository);
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

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                SeedEntraActors(db);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }

    public sealed class SimpleFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("APP_AUTH_MODE", "simple");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = SimpleAdminAuthHandler.AuthenticationSchemeName;
                        options.DefaultChallengeScheme = SimpleAdminAuthHandler.AuthenticationSchemeName;
                        options.DefaultForbidScheme = SimpleAdminAuthHandler.AuthenticationSchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, SimpleAdminAuthHandler>(
                        SimpleAdminAuthHandler.AuthenticationSchemeName,
                        _ => { });

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                db.Users.Add(new User
                {
                    Id = SimpleAdminId,
                    Username = "admin",
                    Role = "admin",
                    Department = "all",
                    FullName = "Simple Administrator",
                    Email = "admin@example.com",
                    PasswordHash = "test-hash",
                });
                db.SaveChanges();
            });
        }

        public HttpClient CreateAuthenticatedClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }

    public sealed class RecordingRepository : IEntraAccessManagementRepository
    {
        public AccessManagementActor? LastActor { get; private set; }
        public EntraAccessSearch? LastSearch { get; private set; }
        public PutEntraOrganizationAccess? LastPut { get; private set; }
        public (string ObjectId, string OrganizationId, string AssignmentId, int ExpectedVersion)? LastRevocation { get; private set; }
        public int PutCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public Exception? NextException { get; set; }

        public void Reset()
        {
            LastActor = null;
            LastSearch = null;
            LastPut = null;
            LastRevocation = null;
            PutCalls = 0;
            UpdateCalls = 0;
            RevokeCalls = 0;
            NextException = null;
        }

        public Task<EntraAccessPage> ListAsync(
            AccessManagementActor actor,
            EntraAccessSearch search,
            CancellationToken cancellationToken = default)
        {
            Capture(actor);
            ThrowIfConfigured();
            LastSearch = search;
            return Task.FromResult(new EntraAccessPage([Aggregate()], "cursor-2"));
        }

        public Task<EntraAccessAggregate?> GetAsync(
            AccessManagementActor actor,
            string objectId,
            CancellationToken cancellationToken = default)
        {
            Capture(actor);
            ThrowIfConfigured();
            return Task.FromResult<EntraAccessAggregate?>(objectId == TargetObjectId ? Aggregate() : null);
        }

        public Task<EntraAccessAggregate> UpdateUserAsync(
            AccessManagementActor actor,
            string objectId,
            UpdateEntraAccessUser request,
            CancellationToken cancellationToken = default)
        {
            Capture(actor);
            UpdateCalls++;
            ThrowIfConfigured();
            return Task.FromResult(Aggregate() with
            {
                IsActive = request.IsActive ?? true,
                AuthorizationVersion = 1,
            });
        }

        public Task<EntraAccessAggregate> PutOrganizationAccessAsync(
            AccessManagementActor actor,
            string objectId,
            string organizationId,
            PutEntraOrganizationAccess request,
            CancellationToken cancellationToken = default)
        {
            Capture(actor);
            PutCalls++;
            LastPut = request;
            ThrowIfConfigured();
            return Task.FromResult(Aggregate() with { AuthorizationVersion = 1 });
        }

        public Task<EntraAccessAggregate> RevokeRoleAsync(
            AccessManagementActor actor,
            string objectId,
            string organizationId,
            string assignmentId,
            int expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Capture(actor);
            RevokeCalls++;
            LastRevocation = (objectId, organizationId, assignmentId, expectedVersion);
            ThrowIfConfigured();
            return Task.FromResult(Aggregate() with { AuthorizationVersion = 1 });
        }

        public Task RecordFailureAuditAsync(
            AccessManagementActor actor,
            string objectId,
            string operation,
            string code,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        private void Capture(AccessManagementActor actor) => LastActor = actor;

        private void ThrowIfConfigured()
        {
            if (NextException is not null)
                throw NextException;
        }
    }

    public sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "TestEntraAccess";

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
                new("azp", "cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
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

    public sealed class SimpleAdminAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "TestSimpleAdmin";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, SimpleAdminId),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, AuthenticationSchemeName)));
        }
    }

    private static void SeedEntraActors(AppDbContext db)
    {
        var organization = new Organization
        {
            Id = OrganizationId,
            Name = "Primary Organization",
            UpdatedBy = "test",
        };
        var department = new Department
        {
            Id = DepartmentId,
            OrganizationId = OrganizationId,
            Name = "Engineering",
            UpdatedBy = "test",
        };
        var admin = CreateEntraUser(AdminObjectId);
        var organizationAdmin = CreateEntraUser(OrganizationAdminObjectId);

        db.AddRange(organization, department, admin, organizationAdmin);
        db.SaveChanges();

        var adminDepartmentMembership = CreateDepartmentMembership(AdminObjectId);
        var organizationAdminDepartmentMembership = CreateDepartmentMembership(OrganizationAdminObjectId);
        db.AddRange(adminDepartmentMembership, organizationAdminDepartmentMembership);
        db.SaveChanges();
        db.AddRange(
            CreateOrganizationMembership(AdminObjectId, adminDepartmentMembership.Id),
            CreateOrganizationMembership(OrganizationAdminObjectId, organizationAdminDepartmentMembership.Id));
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
            });
        db.SaveChanges();
    }

    private static User CreateEntraUser(string objectId) => new()
    {
        Id = objectId,
        AuthenticationProvider = "entra",
        EntraTenantId = TenantId,
        EntraObjectId = objectId,
        Username = $"{objectId[..8]}@example.com",
        FullName = "Test Administrator",
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

    private static OrganizationMembership CreateOrganizationMembership(string userId, string defaultMembershipId) => new()
    {
        UserId = userId,
        OrganizationId = OrganizationId,
        DefaultDepartmentMembershipId = defaultMembershipId,
        UpdatedBy = "test",
    };

    private static EntraAccessAggregate Aggregate() => new(
        TargetObjectId,
        "target@example.com",
        "Target User",
        "target@example.com",
        true,
        0,
        [new EntraOrganizationAccess(
            OrganizationId,
            "active",
            [DepartmentId],
            DepartmentId,
            [new EntraAccessRoleAssignment(
                AssignmentId,
                "recruiter",
                OrganizationId,
                DepartmentId,
                "delegated",
                "active")])]);

    public sealed record PutRequest(
        int ExpectedVersion,
        ProfileRequest Profile,
        MembershipRequest Membership,
        IReadOnlyList<RoleRequest> RoleAssignments);

    public sealed record ProfileRequest(string Username, string FullName, string? Email);
    public sealed record MembershipRequest(string Status, IReadOnlyList<string> DepartmentIds, string? DefaultDepartmentId);
    public sealed record RoleRequest(string Role, string? DepartmentId);

    private static class HttpMethodNames
    {
        public const string Get = "GET";
        public const string Put = "PUT";
        public const string Patch = "PATCH";
        public const string Delete = "DELETE";
    }
}