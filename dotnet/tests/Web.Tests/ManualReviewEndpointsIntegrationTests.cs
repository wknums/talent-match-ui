using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TalentMatch.Web.Tests;

public class ManualReviewEndpointsIntegrationTests : IClassFixture<UserManagementIntegrationTests.TestFactory>
{
    private readonly UserManagementIntegrationTests.TestFactory _factory;

    public ManualReviewEndpointsIntegrationTests(UserManagementIntegrationTests.TestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetManualReview_WithoutSavedReview_ReturnsOkNull()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Username = "admin",
            Password = "adm1n99"
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync($"/api/applications/{Guid.NewGuid()}/manual-review");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("null");
    }
}