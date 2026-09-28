using System.Net;
using FluentAssertions;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class ApiClientJobScopeTests
{
    [Fact]
    public async Task ListJobScopes_RecruiterUsesEffectiveNamedScopesWithoutAdminEndpoint()
    {
        var handler = new MockHttpHandler();
        handler.SetupResponse("GET", "/api/auth/me", HttpStatusCode.OK, """
            {
              "userId": "user-1",
              "tenantId": "tenant-1",
              "objectId": "object-1",
              "username": "recruiter@contoso.com",
              "fullName": "Contoso Recruiter",
              "email": "recruiter@contoso.com",
              "globalRole": null,
              "authorizationVersion": 1,
              "memberships": [{
                "organizationId": "org-contoso",
                "organizationName": "Contoso",
                "defaultDepartmentId": "dept-sales",
                "departments": [
                  { "departmentId": "dept-sales", "departmentName": "Sales" },
                  { "departmentId": "dept-hr", "departmentName": "Human Resources" },
                  { "departmentId": "dept-panel", "departmentName": "Executive Panel" }
                ]
              }],
              "authorizations": [
                { "role": "recruiter", "roleLabel": "Recruiter", "organizationId": "org-contoso", "departmentId": "dept-sales", "assignmentSource": "delegated" },
                { "role": "recruiter", "roleLabel": "Recruiter", "organizationId": "org-contoso", "departmentId": "dept-hr", "assignmentSource": "delegated" },
                { "role": "business_panel", "roleLabel": "Business Panel", "organizationId": "org-contoso", "departmentId": "dept-panel", "assignmentSource": "delegated" }
              ],
              "tokenIssuedAt": "2026-08-19T10:00:00Z",
              "refreshRequiredAt": "2026-08-19T10:15:00Z"
            }
            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://app.example/") };
        var api = new ApiClient(httpClient, new PublicAuthConfiguration("entra"));

        var organizations = await api.ListJobScopeOrganizationsAsync();

        organizations.Should().ContainSingle().Which.Name.Should().Be("Contoso");
        organizations.Single().Departments.Select(department => department.Name)
            .Should().Equal("Sales", "Human Resources");
    }
}