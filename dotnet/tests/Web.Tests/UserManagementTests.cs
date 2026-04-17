using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public class UserManagementTests : BunitContext
{
    private readonly MockHttpHandler _handler;

    public UserManagementTests()
    {
        _handler = new MockHttpHandler();
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        Services.AddSingleton(new ApiClient(httpClient));
    }

    [Fact]
    public async Task CreateUser_Failure_ShowsErrorAndKeepsFormOpen()
    {
        // Arrange
        _handler.SetupResponse("GET", "/api/users", HttpStatusCode.OK, "[]");
        _handler.SetupResponse("GET", "/api/users/reset-requests", HttpStatusCode.OK, "[]");
        _handler.SetupResponse("POST", "/api/users", HttpStatusCode.Conflict,
            "Username 'testuser' already exists.");

        var cut = Render<UserManagement>();

        // Open create form
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Create User")).Click());

        // Fill in form fields one at a time, re-querying after each change
        await cut.InvokeAsync(() => cut.FindAll("input")[0].Change("testuser"));     // Username
        await cut.InvokeAsync(() => cut.FindAll("input")[1].Change("Test User"));    // Full Name
        await cut.InvokeAsync(() => cut.FindAll("input")[2].Change("test@test.com")); // Email
        // Index 3 is Department (select for role is not an input)
        await cut.InvokeAsync(() => cut.FindAll("input")[3].Change("Engineering"));  // Department
        await cut.InvokeAsync(() => cut.FindAll("input")[4].Change("pass123"));      // Password

        // Click "Create" button
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Create").Click());

        // Assert: error message is shown
        cut.Markup.Should().Contain("already exists",
            because: "the component should display the server error message");

        // Assert: form should still be open
        cut.Markup.Should().Contain("Create New User",
            because: "the create form should remain open on failure");
    }

    [Fact]
    public async Task CreateUser_Success_ClosesFormAndShowsSuccessMessage()
    {
        // Arrange: first GET returns empty, after creation returns new user
        _handler.SetupSequentialResponse("GET", "/api/users",
            new MockHttpHandler.SequentialResponse(HttpStatusCode.OK, "[]"),
            new MockHttpHandler.SequentialResponse(HttpStatusCode.OK,
                JsonSerializer.Serialize(new[] { new { Id = "u1", Username = "newuser", Role = "recruiter", Department = "Sales", FullName = "New User", Email = "new@test.com" } })));
        _handler.SetupResponse("GET", "/api/users/reset-requests", HttpStatusCode.OK, "[]");
        _handler.SetupResponse("POST", "/api/users", HttpStatusCode.Created,
            JsonSerializer.Serialize(new { Id = "u1", Username = "newuser", Role = "recruiter", Department = "Sales" }));

        var cut = Render<UserManagement>();

        // Open create form
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Create User")).Click());

        // Fill in form fields one at a time
        await cut.InvokeAsync(() => cut.FindAll("input")[0].Change("newuser"));
        await cut.InvokeAsync(() => cut.FindAll("input")[1].Change("New User"));
        await cut.InvokeAsync(() => cut.FindAll("input")[2].Change("new@test.com"));
        await cut.InvokeAsync(() => cut.FindAll("input")[3].Change("Sales"));
        await cut.InvokeAsync(() => cut.FindAll("input")[4].Change("pass123"));

        // Click "Create" button
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Create").Click());

        // Assert: success message shown
        cut.Markup.Should().Contain("User created successfully");

        // Assert: form should be closed
        cut.Markup.Should().NotContain("Create New User",
            because: "the create form should close on success");
    }

    [Fact]
    public async Task EditUser_CurrentUserRoleChange_IsDisabledAndShowsHint()
    {
        var users = JsonSerializer.Serialize(new[]
        {
            new { Id = "admin-1", Username = "admin", Role = "admin", Department = "Leadership", FullName = "Administrator", Email = "admin@test.com" },
            new { Id = "user-2", Username = "recruiter", Role = "recruiter", Department = "Engineering", FullName = "Recruiter User", Email = "recruiter@test.com" },
        });

        _handler.SetupResponse("GET", "/api/auth/me", HttpStatusCode.OK,
            JsonSerializer.Serialize(new { Id = "admin-1", Username = "admin", Role = "admin", Department = "Leadership", FullName = "Administrator", Email = "admin@test.com" }));
        _handler.SetupResponse("GET", "/api/users", HttpStatusCode.OK, users);
        _handler.SetupResponse("GET", "/api/users/reset-requests", HttpStatusCode.OK, "[]");

        var cut = Render<UserManagement>();

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Any(button => button.TextContent.Trim() == "Edit").Should().BeTrue());

        await cut.InvokeAsync(() => cut.FindAll("button").First(button => button.TextContent.Trim() == "Edit").Click());

        cut.Markup.Should().Contain("Edit User: admin");
        cut.Markup.Should().Contain("You cannot change your own role.");
        cut.Find("select").GetAttribute("disabled").Should().NotBeNull();
    }
}

public class MockHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _staticResponses = new();
    private readonly Dictionary<string, HttpStatusCode> _staticStatusCodes = new();
    private readonly Dictionary<string, Queue<(HttpStatusCode, string)>> _sequentialResponses = new();

    public record SequentialResponse(HttpStatusCode StatusCode, string Body);

    public void SetupResponse(string method, string path, HttpStatusCode statusCode, string body)
    {
        var key = $"{method}:{path}";
        _staticResponses[key] = body;
        _staticStatusCodes[key] = statusCode;
    }

    public void SetupSequentialResponse(string method, string path, params SequentialResponse[] responses)
    {
        var key = $"{method}:{path}";
        var queue = new Queue<(HttpStatusCode, string)>();
        foreach (var r in responses)
            queue.Enqueue((r.StatusCode, r.Body));
        _sequentialResponses[key] = queue;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = $"{request.Method}:{request.RequestUri?.AbsolutePath}";

        if (_sequentialResponses.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            var (sc, body) = queue.Dequeue();
            return Task.FromResult(new HttpResponseMessage(sc)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        if (_staticResponses.TryGetValue(key, out var responseBody))
        {
            return Task.FromResult(new HttpResponseMessage(_staticStatusCodes[key])
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
