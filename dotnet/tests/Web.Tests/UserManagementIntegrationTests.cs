using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public class UserManagementIntegrationTests : IClassFixture<UserManagementIntegrationTests.TestFactory>, IDisposable
{
    private readonly TestFactory _factory;

    public UserManagementIntegrationTests(TestFactory factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        // Each test gets a fresh factory via the class fixture
    }

    /// <summary>
    /// Custom factory that keeps a SQLite in-memory connection open for the duration of the test.
    /// </summary>
    public class TestFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Remove existing DbContext registration
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                    services.Remove(descriptor);

                // Create and open a persistent in-memory SQLite connection
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();

                services.AddDbContext<AppDbContext>(options =>
                    options.UseSqlite(_connection));

                // Build a temporary service provider to create the database schema
                var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }

    private HttpClient CreateAuthenticatedAdminClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        return client;
    }

    private static async Task LoginAsAdmin(HttpClient client)
    {
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "adm1n99" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "admin login should succeed with seeded credentials");
    }

    private static string UniqueValue(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static async Task<string> CreateUserAsync(
        HttpClient client,
        string username,
        string email,
        string role = "recruiter",
        string department = "Engineering",
        string password = "test123",
        string fullName = "Test User")
    {
        var response = await client.PostAsJsonAsync("/api/users", new
        {
            Username = username,
            Role = role,
            Department = department,
            Password = password,
            FullName = fullName,
            Email = email,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/users");
        users.Should().NotBeNull();

        return users!
            .Single(user => user.Username == username)
            .Id;
    }

    [Fact]
    public async Task CreateUser_ThenListUsers_ThenLoginAsNewUser_ThenVerifyMe()
    {
        // Arrange
        var client = CreateAuthenticatedAdminClient();
        await LoginAsAdmin(client);

        // Act 1: Create a new user
        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            Username = "testrecruiter",
            Role = "recruiter",
            Department = "Engineering",
            Password = "test123",
            FullName = "Test Recruiter",
            Email = "test@example.com"
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, "creating a new user should succeed");

        // Act 2: Verify user appears in GET /api/users
        var usersResponse = await client.GetFromJsonAsync<List<UserDto>>("/api/users");
        usersResponse.Should().NotBeNull();
        usersResponse!.Should().Contain(u => u.Username == "testrecruiter",
            because: "the newly created user should appear in the user list");

        // Act 3: Logout admin
        await client.PostAsync("/api/auth/logout", null);

        // Act 4: Login as the newly created user
        var newUserLoginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Username = "testrecruiter",
            Password = "test123"
        });
        newUserLoginResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the newly created user should be able to log in with the password set during creation");

        // Act 5: Verify GET /api/auth/me returns the new user's data
        var meResponse = await client.GetFromJsonAsync<UserDto>("/api/auth/me");
        meResponse.Should().NotBeNull();
        meResponse!.Username.Should().Be("testrecruiter");
        meResponse.Role.Should().Be("recruiter");
        meResponse.Department.Should().Be("Engineering");
    }

    [Fact]
    public async Task CreateDuplicateUser_Returns409Conflict()
    {
        // Arrange
        var client = CreateAuthenticatedAdminClient();
        await LoginAsAdmin(client);

        var userPayload = new
        {
            Username = "duplicateuser",
            Role = "recruiter",
            Department = "Sales",
            Password = "pass123",
            FullName = "Dup User",
            Email = "dup@example.com"
        };

        // Act: Create user first time (should succeed)
        var firstResponse = await client.PostAsJsonAsync("/api/users", userPayload);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act: Create same user again (should fail with 409)
        var secondResponse = await client.PostAsJsonAsync("/api/users", userPayload);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "creating a duplicate user should return 409 Conflict");

        var body = await secondResponse.Content.ReadAsStringAsync();
        body.Should().Contain("already exists",
            because: "the error message should indicate the username is taken");
    }

    [Fact]
    public async Task UnauthenticatedRequest_Returns401()
    {
        // Arrange: create a fresh client without logging in
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        // Act: try to list users without authentication
        var response = await client.GetAsync("/api/users");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateUser_ReturnsUpdatedPayload_AndRoundTripsDepartments()
    {
        var client = CreateAuthenticatedAdminClient();
        await LoginAsAdmin(client);

        var username = UniqueValue("edituser");
        var email = $"{username}@example.com";
        var userId = await CreateUserAsync(client, username, email);

        var response = await client.PutAsJsonAsync($"/api/users/{userId}", new
        {
            FullName = "Updated Recruiter",
            Email = $"updated-{username}@example.com",
            Role = "business_panel",
            Department = "Engineering,HR",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedUser = await response.Content.ReadFromJsonAsync<UserDto>();
        updatedUser.Should().NotBeNull();
        updatedUser!.FullName.Should().Be("Updated Recruiter");
        updatedUser.Email.Should().Be($"updated-{username}@example.com");
        updatedUser.Role.Should().Be("business_panel");
        updatedUser.Department.Should().Be("Engineering,HR");

        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/users");
        users.Should().Contain(user =>
            user.Id == userId &&
            user.Role == "business_panel" &&
            user.Department == "Engineering,HR");
    }

    [Fact]
    public async Task UpdateUser_DuplicateEmail_Returns409Conflict()
    {
        var client = CreateAuthenticatedAdminClient();
        await LoginAsAdmin(client);

        var firstUsername = UniqueValue("first");
        var secondUsername = UniqueValue("second");
        var firstId = await CreateUserAsync(client, firstUsername, $"{firstUsername}@example.com");
        _ = await CreateUserAsync(client, secondUsername, $"{secondUsername}@example.com");

        var response = await client.PutAsJsonAsync($"/api/users/{firstId}", new
        {
            FullName = "First User",
            Email = $"{secondUsername}@example.com",
            Role = "recruiter",
            Department = "Engineering",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("already in use");
    }

    [Fact]
    public async Task UpdateUser_NotFound_Returns404NotFound()
    {
        var client = CreateAuthenticatedAdminClient();
        await LoginAsAdmin(client);

        var response = await client.PutAsJsonAsync("/api/users/missing-user", new
        {
            FullName = "Missing User",
            Email = "missing@example.com",
            Role = "recruiter",
            Department = "Engineering",
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateUser_InvalidPayload_Returns400BadRequest()
    {
        var client = CreateAuthenticatedAdminClient();
        await LoginAsAdmin(client);

        var username = UniqueValue("invalid");
        var userId = await CreateUserAsync(client, username, $"{username}@example.com");

        var response = await client.PutAsJsonAsync($"/api/users/{userId}", new
        {
            FullName = "",
            Email = "not-an-email",
            Role = "viewer",
            Department = "Engineering",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("FullName");
        body.Should().Contain("Role");
    }

    [Fact]
    public async Task UpdateUser_SelfRoleChange_PreservesAdminRole()
    {
        var client = CreateAuthenticatedAdminClient();
        await LoginAsAdmin(client);

        var currentUser = await client.GetFromJsonAsync<UserDto>("/api/auth/me");
        currentUser.Should().NotBeNull();

        var response = await client.PutAsJsonAsync($"/api/users/{currentUser!.Id}", new
        {
            FullName = "Administrator Updated",
            Email = "admin-updated@example.com",
            Role = "recruiter",
            Department = "Leadership,Operations",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedUser = await response.Content.ReadFromJsonAsync<UserDto>();
        updatedUser.Should().NotBeNull();
        updatedUser!.Role.Should().Be("admin");
        updatedUser.FullName.Should().Be("Administrator Updated");
        updatedUser.Department.Should().Be("Leadership,Operations");

        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/users");
        users.Should().Contain(user =>
            user.Id == currentUser.Id &&
            user.Role == "admin" &&
            user.FullName == "Administrator Updated");
    }

    private record UserDto(string Id, string Username, string Role, string Department, string? FullName, string? Email, DateTime CreatedAt = default, DateTime? LastLogin = null);
}
