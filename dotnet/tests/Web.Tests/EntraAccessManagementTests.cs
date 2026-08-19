using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Layout;
using TalentMatch.Web.Client.Pages;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class EntraAccessManagementTests : BunitContext
{
    private const string TargetObjectId = "20000000-0000-4000-8000-000000000001";
    private const string OrganizationId = "30000000-0000-4000-8000-000000000001";
    private const string DepartmentId = "40000000-0000-4000-8000-000000000001";
    private const string AssignmentId = "50000000-0000-4000-8000-000000000001";

    [Fact]
    public void UserAdministration_SimpleModePreservesLocalUserManagement()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/auth/me", HttpStatusCode.OK, SimpleUser("admin"));
        handler.Enqueue("GET", "/api/users", HttpStatusCode.OK, "[]");
        handler.Enqueue("GET", "/api/users/reset-requests", HttpStatusCode.OK, "[]");
        RegisterServices(handler, PublicAuthConfiguration.Simple("http://localhost/"));

        var cut = Render<UserAdministration>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("User Management"));
        cut.Markup.Should().NotContain("Entra Access Management");
        cut.Markup.Should().Contain("Reset Requests");
    }

    [Fact]
    public void UserAdministration_EntraModeShowsAccessManagementWithoutPasswordControls()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/auth/me", HttpStatusCode.OK, EntraAuthorization("organization_admin"));
        handler.Enqueue("GET", "/api/organizations", HttpStatusCode.OK, OrganizationList());
        handler.Enqueue("GET", "/api/access-management/users", HttpStatusCode.OK, UserPage());
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<UserAdministration>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Entra Access Management"));
        cut.Markup.Should().NotContain("Create User");
        cut.Markup.Should().NotContain("Reset Requests");
    }

    [Fact]
    public void NavMenu_ShowsAdministrationOnlyToModeAppropriateAuthorities()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/auth/me", HttpStatusCode.OK, EntraAuthorization("organization_admin"));
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Access management"));
        cut.Markup.Should().NotContain("Failure Queue");
    }

    [Fact]
    public async Task OrganizationAdmin_CanInspectButCannotSeeGlobalLifecycleOrPeerGrant()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/organizations", HttpStatusCode.OK, OrganizationList());
        handler.Enqueue("GET", "/api/access-management/users", HttpStatusCode.OK, UserPage());
        handler.Enqueue("GET", $"/api/access-management/users/{TargetObjectId}", HttpStatusCode.OK, AccessUser());
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<EntraAccessManagement>(parameters => parameters
            .Add(component => component.GlobalAdmin, false));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Target User"));

        await cut.InvokeAsync(() => cut.Find("button[data-action='inspect-user']").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(TargetObjectId));
        cut.Markup.Should().NotContain("Disable identity");
        cut.Markup.Should().NotContain("Reactivate identity");
        cut.Markup.ToLowerInvariant().Should().NotContain("organization admin");
    }

    [Fact]
    public async Task OrganizationAdministration_SelectsMemberByNameAndLoadsAccessAutomatically()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/auth/me", HttpStatusCode.OK, EntraAuthorization("admin"));
        handler.Enqueue("GET", "/api/organizations", HttpStatusCode.OK, OrganizationList());
        handler.Enqueue("GET", "/api/access-management/users", HttpStatusCode.OK,
            UserPage(AccessUser(organizationId: OrganizationId, includeAssignment: true)));
        handler.Enqueue("GET", $"/api/access-management/users/{TargetObjectId}", HttpStatusCode.OK,
            AccessUser(organizationId: OrganizationId, includeAssignment: true));
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<OrganizationAdmin>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Target User — target@example.com"));

        await cut.InvokeAsync(() => cut.Find("#organization-member-select").Change(TargetObjectId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Recruiter"));
        cut.Markup.Should().Contain("Creates and manages jobs in a selected department.");
        cut.Markup.Should().Contain("TalentMatch permissions, not Entra or Azure roles.");
        cut.Markup.Should().NotContain("Enter the target user's Entra object ID.");
        cut.Markup.Should().NotContain("Load assignments");
        handler.Requests.Should().ContainSingle(request =>
            request.Path == $"/api/access-management/users/{TargetObjectId}");
    }

    [Fact]
    public async Task Onboarding_RequiresExplicitDefaultThenConfirmsDesiredState()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/organizations", HttpStatusCode.OK, OrganizationList());
        handler.Enqueue("GET", "/api/access-management/users", HttpStatusCode.OK, UserPage());
        handler.Enqueue("GET", $"/api/access-management/users/{TargetObjectId}", HttpStatusCode.OK, AccessUser());
        handler.Enqueue("PUT", $"/api/access-management/users/{TargetObjectId}/organizations/{OrganizationId}",
            HttpStatusCode.OK, AccessUser(version: 1, organizationId: OrganizationId));
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<EntraAccessManagement>(parameters => parameters
            .Add(component => component.GlobalAdmin, true));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Target User"));
        await cut.InvokeAsync(() => cut.Find("button[data-action='inspect-user']").Click());
        cut.WaitForElement("#entra-organization-id");

        await cut.InvokeAsync(() => cut.Find("#entra-organization-id").Change(OrganizationId));
        cut.Markup.Should().Contain("Contoso Talent");
        cut.Markup.Should().Contain("Recruitment Operations");
        await cut.InvokeAsync(() => cut.Find(".entra-access__checks input[type='checkbox']").Change(true));
        await cut.InvokeAsync(() => cut.Find("button[data-action='review-access']").Click());
        cut.Find("[role='alert']").TextContent.Should().Contain("explicit default");

        await cut.InvokeAsync(() => cut.Find("#entra-default-department").Change(DepartmentId));
        await cut.InvokeAsync(() => cut.Find("#entra-role-department").Change(DepartmentId));
        await cut.InvokeAsync(() => cut.Find(".entra-access__role-editor button").Click());
        cut.Markup.Should().Contain("Recruiter");
        await cut.InvokeAsync(() => cut.Find("button[data-action='review-access']").Click());
        cut.Markup.Should().Contain("Review organization access");
        await cut.InvokeAsync(() => cut.Find("button[data-action='confirm-access']").Click());

        cut.WaitForAssertion(() => cut.Find("[role='status']").TextContent.Should().Contain("Access updated"));
        var request = handler.Requests.Single(item => item.Method == "PUT");
        request.Body.Should().Contain($"\"defaultDepartmentId\":\"{DepartmentId}\"");
        request.Body.Should().Contain("\"role\":\"recruiter\"");
        request.Body.Should().Contain("\"expectedVersion\":0");
    }

    [Fact]
    public async Task TargetedRevoke_ConfirmsAndDeletesOnlySelectedAssignment()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/organizations", HttpStatusCode.OK, OrganizationList());
        handler.Enqueue("GET", "/api/access-management/users", HttpStatusCode.OK,
            UserPage(AccessUser(organizationId: OrganizationId, includeAssignment: true)));
        handler.Enqueue("GET", $"/api/access-management/users/{TargetObjectId}", HttpStatusCode.OK,
            AccessUser(organizationId: OrganizationId, includeAssignment: true));
        handler.Enqueue("DELETE",
            $"/api/access-management/users/{TargetObjectId}/organizations/{OrganizationId}/role-assignments/{AssignmentId}",
            HttpStatusCode.OK, AccessUser(version: 1, organizationId: OrganizationId));
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<EntraAccessManagement>(parameters => parameters
            .Add(component => component.GlobalAdmin, false));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Target User"));
        await cut.InvokeAsync(() => cut.Find("button[data-action='inspect-user']").Click());
        cut.WaitForElement("button[data-action='revoke-role']");
        await cut.InvokeAsync(() => cut.Find("button[data-action='revoke-role']").Click());
        await cut.InvokeAsync(() => cut.Find("button[data-action='confirm-revoke']").Click());

        cut.WaitForAssertion(() => cut.Find("[role='status']").TextContent.Should().Contain("Role access revoked"));
        handler.Requests.Should().ContainSingle(item =>
            item.Method == "DELETE"
            && item.Path.EndsWith($"/{AssignmentId}", StringComparison.Ordinal)
            && item.Query == "?expectedVersion=0");
    }

    [Fact]
    public async Task Conflict_RemainsVisibleWithCorrelationId()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/organizations", HttpStatusCode.OK, OrganizationList());
        handler.Enqueue("GET", "/api/access-management/users", HttpStatusCode.OK, UserPage());
        handler.Enqueue("GET", $"/api/access-management/users/{TargetObjectId}", HttpStatusCode.OK, AccessUser());
        handler.Enqueue("PATCH", $"/api/access-management/users/{TargetObjectId}", HttpStatusCode.Conflict,
            """{"error":"version_conflict","message":"Authorization state changed. Refresh and retry.","correlationId":"correlation-409"}""",
            "correlation-409");
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<EntraAccessManagement>(parameters => parameters
            .Add(component => component.GlobalAdmin, true));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Target User"));
        await cut.InvokeAsync(() => cut.Find("button[data-action='inspect-user']").Click());
        cut.WaitForElement("button[data-action='toggle-identity']");
        await cut.InvokeAsync(() => cut.Find("button[data-action='toggle-identity']").Click());
        await cut.InvokeAsync(() => cut.Find("button[data-action='confirm-identity']").Click());

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[role='alert']").TextContent;
            alert.Should().Contain("changed by another administrator");
            alert.Should().Contain("correlation-409");
        });
    }

    [Fact]
    public void InitialLoadFailure_IsVisibleAndRetryable()
    {
        var handler = new AccessManagementHttpHandler();
        handler.Enqueue("GET", "/api/organizations", HttpStatusCode.OK, OrganizationList());
        handler.Enqueue("GET", "/api/access-management/users", HttpStatusCode.ServiceUnavailable,
            """{"error":"service_unavailable","message":"Access service is unavailable.","correlationId":"correlation-503"}""");
        RegisterServices(handler, EntraConfiguration());

        var cut = Render<EntraAccessManagement>(parameters => parameters
            .Add(component => component.GlobalAdmin, true));

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.Should().Contain("correlation-503"));
        cut.Markup.Should().Contain("Retry");
    }

    private void RegisterServices(AccessManagementHttpHandler handler, PublicAuthConfiguration configuration)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        Services.AddSingleton(configuration);
        Services.AddSingleton(new ApiClient(client, configuration));
    }

    private static PublicAuthConfiguration EntraConfiguration() => new(
        "entra",
        TenantId: "10000000-0000-4000-8000-000000000001",
        ClientId: "client-id",
        Authority: "https://login.microsoftonline.com/tenant",
        ApiScope: "api://talentmatch/access_as_user",
        ApiBaseAddress: "http://localhost/");

    private static string SimpleUser(string role) => JsonSerializer.Serialize(new
    {
        id = "user-1",
        username = "admin",
        role,
        department = "Operations",
        fullName = "Admin User",
        email = "admin@example.com",
    });

    private static string EntraAuthorization(string role) => JsonSerializer.Serialize(new
    {
        userId = "user-1",
        tenantId = "10000000-0000-4000-8000-000000000001",
        objectId = "10000000-0000-4000-8000-000000000002",
        username = "admin@example.com",
        fullName = "Admin User",
        email = "admin@example.com",
        globalRole = role == "admin" ? "admin" : null,
        authorizationVersion = 3,
        memberships = new[]
        {
            new
            {
                organizationId = OrganizationId,
                organizationName = "Primary",
                defaultDepartmentId = DepartmentId,
                departments = new[] { new { departmentId = DepartmentId, departmentName = "Engineering" } },
            },
        },
        authorizations = role == "organization_admin"
            ? new[] { new { role, roleLabel = "Organization Admin", organizationId = OrganizationId, departmentId = (string?)null, assignmentSource = "delegated" } }
            : Array.Empty<object>(),
        tokenIssuedAt = "2026-08-01T10:00:00Z",
        refreshRequiredAt = "2026-08-01T10:15:00Z",
    });

    private static string UserPage(string? user = null) =>
        $"{{\"items\":[{user ?? AccessUser()}],\"nextCursor\":null}}";

    private static string OrganizationList() => JsonSerializer.Serialize(new[]
    {
        new
        {
            id = OrganizationId,
            name = "Contoso Talent",
            status = "active",
            departments = new[]
            {
                new
                {
                    id = DepartmentId,
                    organizationId = OrganizationId,
                    name = "Recruitment Operations",
                    status = "active",
                },
            },
        },
    });

    private static string AccessUser(
        int version = 0,
        string? organizationId = null,
        bool includeAssignment = false)
    {
        var organizations = organizationId is null
            ? Array.Empty<object>()
            : new object[]
            {
                new
                {
                    organizationId,
                    status = "active",
                    departmentIds = new[] { DepartmentId },
                    defaultDepartmentId = DepartmentId,
                    roleAssignments = includeAssignment
                        ? new[]
                        {
                            new
                            {
                                id = AssignmentId,
                                role = "recruiter",
                                organizationId,
                                departmentId = DepartmentId,
                                source = "delegated",
                                status = "active",
                            },
                        }
                        : Array.Empty<object>(),
                },
            };

        return JsonSerializer.Serialize(new
        {
            objectId = TargetObjectId,
            username = "target@example.com",
            fullName = "Target User",
            email = "target@example.com",
            isActive = true,
            authorizationVersion = version,
            organizations,
        });
    }
}

internal sealed class AccessManagementHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Response>> _responses = new(StringComparer.Ordinal);

    public List<Request> Requests { get; } = [];

    public void Enqueue(
        string method,
        string path,
        HttpStatusCode status,
        string body,
        string? correlationId = null)
    {
        var key = $"{method}:{path}";
        if (!_responses.TryGetValue(key, out var queue))
        {
            queue = new Queue<Response>();
            _responses.Add(key, queue);
        }
        queue.Enqueue(new Response(status, body, correlationId));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var query = request.RequestUri?.Query ?? string.Empty;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Request(request.Method.Method, path, query, body));

        var key = $"{request.Method.Method}:{path}";
        if (!_responses.TryGetValue(key, out var queue) || queue.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var configured = queue.Dequeue();
        var response = new HttpResponseMessage(configured.Status)
        {
            Content = new StringContent(configured.Body, Encoding.UTF8, "application/json"),
        };
        if (configured.CorrelationId is not null)
            response.Headers.Add("X-Correlation-ID", configured.CorrelationId);
        return response;
    }

    public sealed record Request(string Method, string Path, string Query, string Body);
    private sealed record Response(HttpStatusCode Status, string Body, string? CorrelationId);
}