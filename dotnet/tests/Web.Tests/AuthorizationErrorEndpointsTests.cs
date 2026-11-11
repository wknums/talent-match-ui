using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace TalentMatch.Web.Tests;

public sealed class AuthorizationErrorEndpointsTests :
    IClassFixture<OrganizationEndpointsTests.OrganizationFactory>
{
    private readonly OrganizationEndpointsTests.OrganizationFactory _factory;

    public AuthorizationErrorEndpointsTests(OrganizationEndpointsTests.OrganizationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateJob_InvalidOrganizationDepartmentPair_ReturnsCanonicalError()
    {
        await _factory.ResetAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Auth", "organization-admin");

        var response = await client.PostAsJsonAsync("/api/jobs", new
        {
            title = "Mismatched scope",
            organization = "Contoso",
            department = "Finance",
            organizationId = "55555555-5555-4555-8555-555555555555",
            departmentId = "99999999-9999-4999-8999-999999999999",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        var correlationId = values!.Single();
        Guid.TryParse(correlationId, out _).Should().BeTrue();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "error", "message", "correlationId");
        payload.GetProperty("error").GetString().Should().Be("invalid_job_scope");
        payload.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("correlationId").GetString().Should().Be(correlationId);
    }
}