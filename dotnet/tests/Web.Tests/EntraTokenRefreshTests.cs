using System.Net;
using System.Net.Http.Json;
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
    public async Task ExplicitlyRetryableBodylessPostRecoversFromStaleToken()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(
            StaleTokenResponse(),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/jobs/job-1/prompts/prompt-1/activate");
        request.Options.Set(StaleTokenRetryHandler.RetryOnStaleToken, true);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Attempts.Should().Be(2);
        invalidator.InvalidationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExplicitlyRetryableJsonPost_PreservesTheBodyAndContentHeaders()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(
            StaleTokenResponse(),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/jobs/job-1/source-connections")
        {
            Content = JsonContent.Create(new { folderUrl = "https://contoso.sharepoint.com/Candidates" }),
        };
        request.Content.Headers.Add("X-Test-Content", "preserved");
        request.Headers.Add("X-Correlation-ID", "connection-correlation");
        request.Options.Set(StaleTokenRetryHandler.RetryOnStaleToken, true);
        var body = await request.Content.ReadAsStringAsync();

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Attempts.Should().Be(2);
        inner.Bodies.Should().Equal(body, body);
        inner.ContentTypes.Should().Equal("application/json; charset=utf-8", "application/json; charset=utf-8");
        inner.ContentHeaders.Should().Equal("preserved", "preserved");
        inner.CorrelationIds.Should().Equal("connection-correlation", "connection-correlation");
        invalidator.InvalidationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExplicitlyRetryableJsonPost_IsNotRetriedMoreThanOnce()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(StaleTokenResponse(), StaleTokenResponse());
        using var client = CreateClient(invalidator, inner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/jobs/job-1/source-connections")
        {
            Content = JsonContent.Create(new { folderUrl = "https://contoso.sharepoint.com/Candidates" }),
        };
        request.Options.Set(StaleTokenRetryHandler.RetryOnStaleToken, true);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.Attempts.Should().Be(2);
        invalidator.InvalidationCount.Should().Be(1);
    }

    [Fact]
    public async Task PromptTestCreationRetriesOnlyThePreEndpointStaleTokenDenialWithTheSameFiles()
    {
        var invalidator = new RecordingInvalidator();
        var inner = new ScriptedHandler(StaleTokenResponse(),
            new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "test-run-1" }) });
        using var client = CreateClient(invalidator, inner);
        var api = new ApiClient(client);

        await api.CreateTestRunAsync("job-1", "prompt-1",
            new[] { new { FileName = "candidate.txt", Content = "Y3Y=", MimeType = "text/plain", SizeBytes = 2 } });

        inner.Attempts.Should().Be(2);
        invalidator.InvalidationCount.Should().Be(1);
        inner.Bodies.Should().HaveCount(2);
        inner.Bodies[0].Should().Be(inner.Bodies[1]);
        inner.Bodies[0].Should().Contain("candidate.txt").And.Contain("Y3Y=");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "wrong_tenant")]
    [InlineData(HttpStatusCode.InternalServerError, "server_error")]
    [InlineData(HttpStatusCode.Conflict, "version_conflict")]
    public async Task PromptTestCreationDoesNotReplayOtherDenials(HttpStatusCode status, string error)
    {
        var invalidator = new RecordingInvalidator();
        var response = AuthErrorResponse(error);
        response.StatusCode = status;
        var inner = new ScriptedHandler(response);
        using var client = CreateClient(invalidator, inner);

        await new ApiClient(client).Invoking(api => api.CreateTestRunAsync("job-1", "prompt-1", Array.Empty<object>()))
            .Should().ThrowAsync<ApiException>();

        inner.Attempts.Should().Be(1);
        invalidator.InvalidationCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptTestCreationDoesNotReplayAnUnknownTransportOutcome(bool timeout)
    {
        var invalidator = new RecordingInvalidator();
        var inner = new FailingTransport(timeout);
        using var client = CreateClient(invalidator, inner);
        var action = () => new ApiClient(client).CreateTestRunAsync("job-1", "prompt-1", Array.Empty<object>());

        if (timeout)
            await action.Should().ThrowAsync<OperationCanceledException>();
        else
            await action.Should().ThrowAsync<HttpRequestException>();
        inner.Attempts.Should().Be(1);
        invalidator.InvalidationCount.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "server_error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    [InlineData(HttpStatusCode.Conflict, "version_conflict")]
    [InlineData(HttpStatusCode.Unauthorized, "wrong_tenant")]
    public async Task ExplicitlyRetryableJsonPost_DoesNotReplayOtherFailures(
        HttpStatusCode status,
        string errorCode)
    {
        var invalidator = new RecordingInvalidator();
        var failure = AuthErrorResponse(errorCode);
        failure.StatusCode = status;
        var inner = new ScriptedHandler(failure, new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(invalidator, inner);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/jobs/job-1/source-connections")
        {
            Content = JsonContent.Create(new { folderUrl = "https://contoso.sharepoint.com/Candidates" }),
        };
        request.Options.Set(StaleTokenRetryHandler.RetryOnStaleToken, true);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(status);
        inner.Attempts.Should().Be(1);
        invalidator.InvalidationCount.Should().Be(0);
        (await response.Content.ReadAsStringAsync()).Should().Contain(errorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitlyRetryableJsonPost_DoesNotReplayAnUnknownTransportOutcome(bool timeout)
    {
        var invalidator = new RecordingInvalidator();
        var inner = new FailingTransport(timeout);
        using var client = CreateClient(invalidator, inner);
        var api = new ApiClient(client);

        var action = () => api.BeginSourceConnectionAsync("job-1", "https://contoso.sharepoint.com/Candidates");

        if (timeout)
            await action.Should().ThrowAsync<OperationCanceledException>();
        else
            await action.Should().ThrowAsync<HttpRequestException>();
        inner.Attempts.Should().Be(1);
        invalidator.InvalidationCount.Should().Be(0);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("preview")]
    [InlineData("discover")]
    [InlineData("confirm")]
    [InlineData("retry")]
    [InlineData("abandon")]
    public async Task SourceConnectionMutations_OptIntoOnePreExecutionAuthenticationRetry(string operation)
    {
        var invalidator = new RecordingInvalidator();
        var nextFailure = AuthErrorResponse("source_unavailable");
        nextFailure.StatusCode = HttpStatusCode.ServiceUnavailable;
        var inner = new ScriptedHandler(StaleTokenResponse(), nextFailure);
        using var client = CreateClient(invalidator, inner);
        var api = new ApiClient(client);
        Func<Task> action = operation switch
        {
            "begin" => () => api.BeginSourceConnectionAsync("job-1", "https://contoso.sharepoint.com/Candidates"),
            "preview" => () => api.PreviewSourceConnectionAsync("connection-1"),
            "discover" => () => api.DiscoverMoreSourceItemsAsync("request-1"),
            "confirm" => () => api.ConfirmBulkScoringRequestAsync("request-1", true, false, 7),
            "retry" => () => api.RetryFailedBulkScoringRequestItemsAsync("request-1", "connection-1"),
            "abandon" => () => api.AbandonBulkScoringRequestDraftAsync("request-1"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        var failure = await action.Should().ThrowAsync<ApiException>();

        failure.Which.StatusCode.Should().Be(503, "only the explicit pre-execution authentication denial is retried");
        inner.Attempts.Should().Be(2);
        inner.Bodies.Distinct().Should().ContainSingle();
        invalidator.InvalidationCount.Should().Be(1);
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
        public List<string> Bodies { get; } = [];
        public List<string?> ContentTypes { get; } = [];
        public List<string?> ContentHeaders { get; } = [];
        public List<string?> CorrelationIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization.Should().BeNull(
                "the authorization handler sits inside this one and is not exercised here");

            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            ContentTypes.Add(request.Content?.Headers.ContentType?.ToString());
            ContentHeaders.Add(request.Content?.Headers.TryGetValues("X-Test-Content", out var contentHeaders) == true
                ? contentHeaders.Single()
                : null);
            CorrelationIds.Add(request.Headers.TryGetValues("X-Correlation-ID", out var correlationIds)
                ? correlationIds.Single()
                : null);
            var response = responses[Math.Min(Attempts, responses.Length - 1)];
            Attempts++;
            return response;
        }
    }

    private sealed class FailingTransport(bool timeout) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return timeout
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("The response timed out."))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("The connection was lost."));
        }
    }
}
