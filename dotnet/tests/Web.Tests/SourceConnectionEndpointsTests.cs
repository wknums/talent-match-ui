using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using Moq;
using TalentMatch.Application.BulkScoringRequests.SourceConnections;

namespace TalentMatch.Web.Tests;

public sealed class SourceConnectionEndpointsTests
{
    private const string FolderUrl =
        "https://tenant-a.sharepoint.com/sites/hr/Shared%20Documents/Candidates";

    [Fact]
    public async Task Begin_authorization_sets_a_secure_expiring_browser_correlation_cookie()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(5);
        var workflow = new Mock<ISourceConnectionWorkflow>();
        using var factory = CorrelationFactory(workflow, "test-state", expiresAt);
        using var client = await factory.LoginAsync("recruiter", "test123");

        using var response = await client.PostAsJsonAsync(
            "https://localhost/api/jobs/job-1/source-connections", new { folderUrl = FolderUrl });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var cookie = SetCookieHeaderValue.Parse(response.Headers.GetValues("Set-Cookie").Single());
        cookie.Name.ToString().Should().StartWith("__Host-TalentMatch.SourceConnection.");
        cookie.HttpOnly.Should().BeTrue();
        cookie.Secure.Should().BeTrue();
        cookie.SameSite.Should().Be(Microsoft.Net.Http.Headers.SameSiteMode.Lax);
        cookie.Path.ToString().Should().Be("/");
        cookie.Domain.HasValue.Should().BeFalse();
        cookie.Expires.Should().BeCloseTo(new DateTimeOffset(expiresAt), TimeSpan.FromSeconds(1));
        cookie.Value.ToString().Should().NotContain("test-state");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("different-state")]
    public async Task Callback_rejects_invalid_browser_correlation_before_redeeming_the_code(string failure)
    {
        var workflow = new Mock<ISourceConnectionWorkflow>();
        using var factory = CorrelationFactory(
            workflow, "test-state", DateTime.UtcNow.AddMinutes(failure == "expired" ? -1 : 5));
        using var owner = await factory.LoginAsync("recruiter", "test123");
        using var begin = await owner.PostAsJsonAsync(
            "https://localhost/api/jobs/job-1/source-connections", new { folderUrl = FolderUrl });
        begin.StatusCode.Should().Be(HttpStatusCode.Created);

        using var otherBrowser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        var callbackState = failure == "different-state" ? "another-state" : "test-state";
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/source-connections/callback?state={callbackState}&code=provider-code");
        if (failure != "missing")
        {
            var cookie = SetCookieHeaderValue.Parse(begin.Headers.GetValues("Set-Cookie").Single());
            var value = cookie.Value.ToString() + (failure == "tampered" ? "tampered" : "");
            request.Headers.Add("Cookie", $"{cookie.Name}={value}");
        }

        using var response = await otherBrowser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("source_connection_invalid_or_expired");
        problem.ToString().Should().NotContain("provider-code").And.NotContain("test-state");
        workflow.Verify(x => x.CompleteCallbackAsync(
            It.IsAny<SourceConnectionCallback>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Callback_accepts_the_initiating_browser_and_consumes_its_correlation_cookie()
    {
        var workflow = new Mock<ISourceConnectionWorkflow>();
        using var factory = CorrelationFactory(workflow, "test-state", DateTime.UtcNow.AddMinutes(5));
        using var client = await factory.LoginAsync("recruiter", "test123");
        using var begin = await client.PostAsJsonAsync(
            "https://localhost/api/jobs/job-1/source-connections", new { folderUrl = FolderUrl });
        begin.StatusCode.Should().Be(HttpStatusCode.Created);
        const string callback = "https://localhost/api/source-connections/callback?state=test-state&code=provider-code";

        using var response = await client.GetAsync(callback);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/jobs/job-1");
        (await client.GetAsync(callback)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        workflow.Verify(x => x.CompleteCallbackAsync(
            It.Is<SourceConnectionCallback>(value =>
                value.State == "test-state" && value.Code == "provider-code"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pending_connections_have_independent_cookies_that_cannot_be_swapped()
    {
        var workflow = new Mock<ISourceConnectionWorkflow>();
        using var factory = CorrelationFactory(workflow, "first-state", DateTime.UtcNow.AddMinutes(5));
        BeginSourceConnectionResponse Pending(string state) => new(
            Guid.NewGuid().ToString(), "authorizing",
            new Uri($"https://login.example.test/authorize?state={state}"), DateTime.UtcNow.AddMinutes(5));
        workflow.SetupSequence(x => x.BeginAsync(
                It.IsAny<SourceConnectionStart>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending("first-state"))
            .ReturnsAsync(Pending("second-state"));
        using var client = await factory.LoginAsync("recruiter", "test123");
        using var first = await client.PostAsJsonAsync(
            "https://localhost/api/jobs/job-1/source-connections", new { folderUrl = FolderUrl });
        using var second = await client.PostAsJsonAsync(
            "https://localhost/api/jobs/job-1/source-connections", new { folderUrl = FolderUrl });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstCookie = SetCookieHeaderValue.Parse(first.Headers.GetValues("Set-Cookie").Single());
        var secondCookie = SetCookieHeaderValue.Parse(second.Headers.GetValues("Set-Cookie").Single());
        firstCookie.Name.Should().NotBe(secondCookie.Name);

        using var otherBrowser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        using var swapped = new HttpRequestMessage(
            HttpMethod.Get, "/api/source-connections/callback?state=second-state&code=provider-code");
        swapped.Headers.Add("Cookie", $"{secondCookie.Name}={firstCookie.Value}");

        (await otherBrowser.SendAsync(swapped)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        workflow.Verify(x => x.CompleteCallbackAsync(
            It.IsAny<SourceConnectionCallback>(), It.IsAny<CancellationToken>()), Times.Never);
        (await client.GetAsync("https://localhost/api/source-connections/callback?state=first-state&code=first-code"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await client.GetAsync("https://localhost/api/source-connections/callback?state=second-state&code=second-code"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);
        workflow.Verify(x => x.CompleteCallbackAsync(
            It.IsAny<SourceConnectionCallback>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static BulkScoringRequestsEndpointsTests.BulkFactory CorrelationFactory(
        Mock<ISourceConnectionWorkflow> workflow, string state, DateTime expiresAt)
    {
        workflow.Setup(x => x.CountUnexpiredPendingAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        workflow.Setup(x => x.BeginAsync(It.IsAny<SourceConnectionStart>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeginSourceConnectionResponse(
                Guid.NewGuid().ToString(),
                "authorizing",
                new Uri(QueryHelpers.AddQueryString("https://login.example.test/authorize", "state", state)),
                expiresAt));
        workflow.Setup(x => x.CompleteCallbackAsync(
                It.IsAny<SourceConnectionCallback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceConnectionCallbackResult(new Uri("/jobs/job-1", UriKind.Relative)));
        return new BulkScoringRequestsEndpointsTests.BulkFactory("mock", configure: services =>
        {
            services.RemoveAll<ISourceConnectionWorkflow>();
            services.AddSingleton(workflow.Object);
        });
    }

    [Fact]
    public async Task Begin_source_connection_returns_only_the_frozen_safe_contract()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/jobs/job-1/source-connections")
        {
            Content = JsonContent.Create(new { folderUrl = FolderUrl }),
        };
        request.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle()
            .Which.Should().Be(correlationId);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "connectionId",
            "status",
            "authorizationUrl",
            "expiresAt");
        Guid.TryParse(payload.GetProperty("connectionId").GetString(), out _).Should().BeTrue();
        payload.GetProperty("status").GetString().Should().Be("verified",
            "deterministic mock mode completes the delegated connection without a live Microsoft login");
        var authorizationUri = new Uri(payload.GetProperty("authorizationUrl").GetString()!);
        authorizationUri.GetLeftPart(UriPartial.Path).Should().Be(
            "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize");
        var authorizationQuery = QueryHelpers.ParseQuery(authorizationUri.Query);
        authorizationQuery["redirect_uri"].Should().ContainSingle().Which.Should().Be(
            "http://localhost/api/source-connections/callback");
        authorizationQuery["response_mode"].Should().ContainSingle().Which.Should().Be("query");
        authorizationQuery["scope"].ToString().Split(' ').Should().Equal(
            "openid",
            "profile",
            "offline_access",
            "https://graph.microsoft.com/Files.Read.All",
            "https://graph.microsoft.com/Sites.Read.All");
        payload.GetProperty("expiresAt").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
        payload.ToString().Should().NotContain(FolderUrl);
    }

    [Fact]
    public async Task Invalid_source_connection_is_an_Rfc9457_problem_with_correlation_and_no_source_disclosure()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");
        var correlationId = Guid.NewGuid().ToString();
        const string prohibitedSource = "https://files.example.test/Secret Candidate/cv.pdf";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/jobs/job-1/source-connections")
        {
            Content = JsonContent.Create(new { folderUrl = prohibitedSource }),
        };
        request.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle()
            .Which.Should().Be(correlationId);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.EnumerateObject().Select(property => property.Name).Should().BeSubsetOf(
            ["type", "title", "status", "detail", "code", "correlationId", "adminConsentUrl"]);
        problem.GetProperty("status").GetInt32().Should().Be(422);
        problem.GetProperty("code").GetString().Should().BeOneOf(
            "source_unsupported_account",
            "source_unsupported_cloud",
            "source_folder_invalid_or_unavailable");
        problem.GetProperty("correlationId").GetString().Should().Be(correlationId);
        problem.ToString().Should().NotContain(prohibitedSource)
            .And.NotContain("Secret Candidate")
            .And.NotContain("cv.pdf");
    }

    [Theory]
    [InlineData("/api/source-connections/callback?error=access_denied&error_description=")]
    [InlineData("/api/source-connections/admin-consent-callback?error=consent_required&error_description=")]
    public async Task Callback_shape_failure_is_public_safe_and_does_not_echo_identity_provider_input(
        string callback)
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var client = factory.CreateClient();
        const string providerSecret = "provider-error-with-sensitive-details";

        using var response = await client.GetAsync(callback + providerSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        problem.ToString().Should().NotContain(providerSecret)
            .And.NotContain("error_description");
    }

    [Fact]
    public async Task Owner_can_read_then_idempotently_abandon_a_connection_without_protected_material_disclosure()
    {
        using var factory = new BulkScoringRequestsEndpointsTests.BulkFactory("mock");
        using var client = await factory.LoginAsync("recruiter", "test123");
        using var begin = await client.PostAsJsonAsync(
            "/api/jobs/job-1/source-connections",
            new { folderUrl = FolderUrl });
        begin.StatusCode.Should().Be(HttpStatusCode.Created);
        var connectionId = (await begin.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionId")
            .GetString();

        using var status = await client.GetAsync($"/api/source-connections/{connectionId}");

        status.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await status.Content.ReadFromJsonAsync<JsonElement>();
        payload.EnumerateObject().Select(property => property.Name).Should().BeSubsetOf(
            [
                "connectionId",
                "status",
                "expiresAt",
                "sourceKind",
                "sourceAccountDisplay",
                "folderName",
                "adminConsentRequired",
                "adminConsentUrl",
                "safeFailureCode",
            ]);
        payload.ToString().Should().NotContain("token")
            .And.NotContain("cache")
            .And.NotContain("verifier")
            .And.NotContain("wrappedKey")
            .And.NotContain(FolderUrl);

        (await client.DeleteAsync($"/api/source-connections/{connectionId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/source-connections/{connectionId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
