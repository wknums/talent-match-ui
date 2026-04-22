using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace TalentMatch.Web.Tests;

public class JobApplicationsIntegrationTests : IClassFixture<UserManagementIntegrationTests.TestFactory>
{
    private readonly UserManagementIntegrationTests.TestFactory _factory;

    public JobApplicationsIntegrationTests(UserManagementIntegrationTests.TestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetApplications_ForNewJob_ReturnsOkEmptyList()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
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

        var createResponse = await client.PostAsJsonAsync("/api/jobs", new
        {
            Title = "Integration Test Job",
            Department = "Engineering",
            Organisation = "TalentMatch",
            PostingDate = DateTime.UtcNow.Date,
            RubricJson = "[]",
            MustHaveCriteriaJson = "[]",
            DesiredCriteriaJson = "[]",
            ScoringRunCount = 3,
            AggregationStrategy = "median",
            LonglistThreshold = 70,
            ShortlistThreshold = 85,
            VarianceThreshold = 15,
            JobDescription = "Test role",
            RubricSource = "manual",
            RawExtractionResponse = (string?)null
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createdJob = await createResponse.Content.ReadFromJsonAsync<CreatedJobResponse>();
        createdJob.Should().NotBeNull();

        var applicationsResponse = await client.GetAsync($"/api/jobs/{createdJob!.Id}/applications");

        applicationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var applications = await applicationsResponse.Content.ReadFromJsonAsync<List<ApplicationListItemResponse>>();
        applications.Should().NotBeNull();
        applications.Should().BeEmpty();
    }

    private sealed class CreatedJobResponse
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ApplicationListItemResponse
    {
        public string Id { get; set; } = string.Empty;
    }
}
