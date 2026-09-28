using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public class ApiAuthorizationMessageHandlerTests : BunitContext
{
    [Fact]
    public void AuthorizationContext_UsesExplicitDefaultWithoutBroadeningScopedAuthorization()
    {
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
                    new DepartmentMembershipResponse("department-authorized", "Authorized first"),
                    new DepartmentMembershipResponse("department-default", "Explicit default"),
                ])],
            [new ScopedAuthorizationResponse(
                "business_panel",
                "Business Panel",
                "organization-1",
                "department-authorized",
                "delegated")],
            DateTimeOffset.Parse("2026-07-31T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-31T10:15:00Z"));

        var user = context.ToUserInfo();

        user.Department.Should().Be("Explicit default");
        user.Role.Should().Be("business_panel");
        context.Authorizations.Should().ContainSingle()
            .Which.DepartmentId.Should().Be("department-authorized");
    }

    [Fact]
    public async Task ApiRequest_AttachesAccessTokenForConfiguredScope()
    {
        const string apiScope = "api://api-id/access_as_user";
        var tokenProvider = new SuccessfulTokenProvider("access-token");
        var captureHandler = new CaptureHandler();
        var configuration = new PublicAuthConfiguration(
            "entra",
            TenantId: "tenant-id",
            ClientId: "client-id",
            Authority: "https://login.microsoftonline.com/tenant-id",
            ApiScope: apiScope,
            ApiBaseAddress: "https://app.example/");
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var authorizationHandler = new ApiAuthorizationMessageHandler(tokenProvider, navigation, configuration)
        {
            InnerHandler = captureHandler,
        };
        using var client = new HttpClient(authorizationHandler)
        {
            BaseAddress = new Uri("https://app.example/"),
        };

        var response = await client.GetAsync("api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captureHandler.AuthorizationScheme.Should().Be("Bearer");
        captureHandler.AuthorizationParameter.Should().Be("access-token");
        tokenProvider.RequestedScopes.Should().Equal(apiScope);
    }

    [Fact]
    public async Task RetriedApiRequest_UsesFreshTokenRatherThanReattachingTheRejectedOne()
    {
        var tokenProvider = new SuccessfulTokenProvider("first-token");
        var captureHandler = new CaptureHandler();
        var configuration = new PublicAuthConfiguration(
            "entra",
            TenantId: "tenant-id",
            ClientId: "client-id",
            Authority: "https://login.microsoftonline.com/tenant-id",
            ApiScope: "api://api-id/access_as_user",
            ApiBaseAddress: "https://app.example/");
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var authorizationHandler = new ApiAuthorizationMessageHandler(tokenProvider, navigation, configuration)
        {
            InnerHandler = captureHandler,
        };
        using var client = new HttpClient(authorizationHandler)
        {
            BaseAddress = new Uri("https://app.example/"),
        };

        await client.GetAsync("api/auth/me");

        // The API rejects tokens older than its freshness window, which is far shorter than the token
        // lifetime. Recovery works only if the retry picks up whatever the provider now holds, so the
        // handler must not keep a private copy of the token it attached the first time.
        tokenProvider.NextTokenValue = "refreshed-token";
        await client.GetAsync("api/auth/me");

        captureHandler.AuthorizationParameter.Should().Be("refreshed-token");
        tokenProvider.RequestCount.Should().Be(2);
    }

    private sealed class SuccessfulTokenProvider(string tokenValue) : IAccessTokenProvider
    {
        public IReadOnlyList<string> RequestedScopes { get; private set; } = [];

        public string NextTokenValue { get; set; } = tokenValue;

        public int RequestCount { get; private set; }

        public ValueTask<AccessTokenResult> RequestAccessToken()
            => RequestAccessToken(new AccessTokenRequestOptions());

        public ValueTask<AccessTokenResult> RequestAccessToken(AccessTokenRequestOptions options)
        {
            RequestCount++;
            RequestedScopes = options.Scopes?.ToArray() ?? [];
            var token = new AccessToken
            {
                Value = NextTokenValue,
                Expires = DateTimeOffset.UtcNow.AddMinutes(60),
                GrantedScopes = RequestedScopes,
            };
            return ValueTask.FromResult(new AccessTokenResult(
                AccessTokenResultStatus.Success,
                token,
                string.Empty,
                new InteractiveRequestOptions
                {
                    Interaction = InteractionType.GetToken,
                    ReturnUrl = "https://app.example/",
                }));
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}