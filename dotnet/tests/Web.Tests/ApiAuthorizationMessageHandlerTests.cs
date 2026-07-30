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

    private sealed class SuccessfulTokenProvider(string tokenValue) : IAccessTokenProvider
    {
        public IReadOnlyList<string> RequestedScopes { get; private set; } = [];

        public ValueTask<AccessTokenResult> RequestAccessToken()
            => RequestAccessToken(new AccessTokenRequestOptions());

        public ValueTask<AccessTokenResult> RequestAccessToken(AccessTokenRequestOptions options)
        {
            RequestedScopes = options.Scopes?.ToArray() ?? [];
            var token = new AccessToken
            {
                Value = tokenValue,
                Expires = DateTimeOffset.UtcNow.AddMinutes(5),
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