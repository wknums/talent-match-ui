using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace TalentMatch.Web.Tests;

public class HealthEndpointsTests : IClassFixture<UserManagementIntegrationTests.TestFactory>
{
    private readonly UserManagementIntegrationTests.TestFactory _factory;

    public HealthEndpointsTests(UserManagementIntegrationTests.TestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsPublicHealthPayload()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>();
        payload.Should().NotBeNull();
        payload!.Status.Should().Be("ok");
        payload.Stack.Should().Be("stack-b");
        payload.Checks.Should().NotBeNull();
        payload.Checks.AzureSql.Status.Should().Be("skipped");
    }

    public sealed class HealthPayload
    {
        public string Status { get; set; } = string.Empty;
        public string Stack { get; set; } = string.Empty;
        public CheckContainer Checks { get; set; } = new();
    }

    public sealed class CheckContainer
    {
        public CheckPayload AwrApi { get; set; } = new();
        public CheckPayload AzureSql { get; set; } = new();
    }

    public sealed class CheckPayload
    {
        public string Status { get; set; } = string.Empty;
    }
}
