using FluentAssertions;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class AuthorizationContextConversionTests
{
    [Fact]
    public void ToUserInfo_UsesExplicitDefaultInsteadOfDepartmentOrAssignmentOrder()
    {
        var issuedAt = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var context = new AuthorizationContextResponse(
            "user-1",
            "tenant-1",
            "object-1",
            "ada@example.com",
            "Ada Lovelace",
            "ada@example.com",
            null,
            7,
            [new OrganizationMembershipResponse(
                "organization-1",
                "Analytical Engines",
                "department-default",
                [
                    new DepartmentMembershipResponse("department-first", "First by array order"),
                    new DepartmentMembershipResponse("department-default", "Explicit default"),
                ])],
            [new ScopedAuthorizationResponse(
                "recruiter",
                "Recruiter",
                "organization-1",
                "department-first",
                "delegated")],
            issuedAt,
            issuedAt.AddMinutes(15));

        var user = context.ToUserInfo();

        user.Department.Should().Be("Explicit default");
    }
}