using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.JSInterop;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public class EntraTokenRefreshTests
{
    [Fact]
    public async Task StaleTokenResponse_PurgesCachedTokenAndRetriesOnce()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(
            StaleTokenResponse(),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);

        var response = await client.GetAsync("api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Attempts.Should().Be(2, "the request is retried exactly once");
        invalidator.InvalidationCount.Should().Be(1);
    }

    [Fact]
    public async Task StaleTokenResponse_IsNotRetriedMoreThanOnce()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(
            StaleTokenResponse(),
            StaleTokenResponse());
        using var client = CreateClient(invalidator, inner);

        var response = await client.GetAsync("api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.Attempts.Should().Be(2);
        invalidator.InvalidationCount.Should().Be(1);
    }

    [Fact]
    public async Task NonIdempotentStaleTokenResponse_IsNotRetried()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(
            StaleTokenResponse(),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);

        var response = await client.PostAsync("api/jobs", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.Attempts.Should().Be(1);
        invalidator.InvalidationCount.Should().Be(0);
    }

    [Fact]
    public async Task UnauthorizedForAnotherReason_IsNotRetriedAndKeepsItsBody()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(
            AuthErrorResponse("wrong_tenant"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);

        var response = await client.GetAsync("api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.Attempts.Should().Be(1);
        invalidator.InvalidationCount.Should().Be(0);
        (await response.Content.ReadAsStringAsync()).Should().Contain("wrong_tenant");
    }

    [Fact]
    public async Task InteractionRequiredDuringRefresh_SurfacesToTheCaller()
    {
        var invalidator = new ThrowingInvalidator();
        var inner = new ScriptedHandler(
            StaleTokenResponse(),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);

        var act = () => client.GetAsync("api/auth/me");

        await act.Should().ThrowAsync<InvalidOperationException>();
        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task UnavailableInterop_FallsBackToTheOriginalDenial()
    {
        var invalidator = new InteropFailureInvalidator();
        var inner = new ScriptedHandler(
            StaleTokenResponse(),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);

        var response = await client.GetAsync("api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.Attempts.Should().Be(1, "a retry would replay the same stale token");
        (await response.Content.ReadAsStringAsync()).Should().Contain("token_stale");
    }

    private static HttpClient CreateClient(
        IAccessTokenCacheInvalidator invalidator,
        HttpMessageHandler inner)
    {
        var handler = new StaleTokenRetryHandler(invalidator) { InnerHandler = inner };
        return new HttpClient(handler) { BaseAddress = new Uri("https://app.example/") };
    }

    private static HttpResponseMessage StaleTokenResponse() => AuthErrorResponse("token_stale");

    private static HttpResponseMessage AuthErrorResponse(string errorCode) =>
        new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                $$"""{"error":"{{errorCode}}","message":"denied","correlationId":"cid"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class RecordingInvalidator : IAccessTokenCacheInvalidator
    {
        public int InvalidationCount { get; private set; }

        public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
        {
            InvalidationCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingInvalidator : IAccessTokenCacheInvalidator
    {
        public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Interactive sign-in is required.");
    }

    private sealed class InteropFailureInvalidator : IAccessTokenCacheInvalidator
    {
        public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
            => throw new JSException("The value 'talentMatchPurgeCachedAccessTokens' is not a function.");
    }

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization.Should().BeNull(
                "the authorization handler sits inside this one and is not exercised here");

            var response = responses[Math.Min(Attempts, responses.Length - 1)];
            Attempts++;
            return Task.FromResult(response);
        }
    }
}
