using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TalentMatch.Web.Server.Middleware;
using TalentMatch.Web.Server.Services;

namespace TalentMatch.Web.Tests;

public sealed class AzureSqlReadinessTests
{
    [Theory]
    [InlineData(40613)]
    [InlineData(40501)]
    [InlineData(40197)]
    [InlineData(10928)]
    [InlineData(10929)]
    [InlineData(49918)]
    [InlineData(49919)]
    [InlineData(49920)]
    [InlineData(-2)]
    public void Classifier_KnownResumeErrors_AreTransient(int errorNumber)
    {
        AzureSqlErrorClassifier.IsTransientErrorNumber(errorNumber).Should().BeTrue();
    }

    [Fact]
    public void Classifier_PublicNetworkDenied_IsPermanent()
    {
        AzureSqlErrorClassifier.IsTransientErrorNumber(47073).Should().BeFalse();
    }

    [Fact]
    public async Task WaitUntilReadyAsync_TransientFailures_RetriesUntilReady()
    {
        var probe = new FakeProbe(
            AzureSqlProbeResult.TransientFailure("database unavailable"),
            AzureSqlProbeResult.TransientFailure("database unavailable"),
            AzureSqlProbeResult.Ready());
        var service = new AzureSqlReadinessService(
            probe,
            new AzureSqlReadinessOptions
            {
                MaxAttempts = 3,
                RetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
                ReadyCacheDuration = TimeSpan.Zero,
            });

        var result = await service.WaitUntilReadyAsync(CancellationToken.None);

        result.Status.Should().Be(AzureSqlReadinessStatus.Ready);
        probe.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_PermanentFailure_DoesNotRetry()
    {
        var probe = new FakeProbe(AzureSqlProbeResult.PermanentFailure("public network denied"));
        var service = new AzureSqlReadinessService(
            probe,
            new AzureSqlReadinessOptions
            {
                MaxAttempts = 3,
                RetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
            });

        var result = await service.WaitUntilReadyAsync(CancellationToken.None);

        result.Status.Should().Be(AzureSqlReadinessStatus.Unavailable);
        result.ErrorCode.Should().Be("database_unavailable");
        probe.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Middleware_TransientExhaustion_ReturnsRetryableServiceUnavailable()
    {
        var endpointCalled = false;
        var middleware = new AzureSqlReadinessMiddleware(_ =>
        {
            endpointCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("/api/jobs");
        var readiness = new FakeReadinessService(
            new AzureSqlReadinessResult(AzureSqlReadinessStatus.Resuming, "database_resuming"));

        await middleware.InvokeAsync(
            context,
            readiness,
            NullLogger<AzureSqlReadinessMiddleware>.Instance);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().Be("10");
        endpointCalled.Should().BeFalse();
        (await ReadErrorCodeAsync(context)).Should().Be("database_resuming");
    }

    [Fact]
    public async Task Middleware_PermanentFailure_ReturnsServiceUnavailableWithoutExecutingEndpoint()
    {
        var endpointCalled = false;
        var middleware = new AzureSqlReadinessMiddleware(_ =>
        {
            endpointCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("/api/auth/me");
        var readiness = new FakeReadinessService(
            new AzureSqlReadinessResult(AzureSqlReadinessStatus.Unavailable, "database_unavailable"));

        await middleware.InvokeAsync(
            context,
            readiness,
            NullLogger<AzureSqlReadinessMiddleware>.Instance);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        endpointCalled.Should().BeFalse();
        readiness.Attempts.Should().Be(1);
        (await ReadErrorCodeAsync(context)).Should().Be("database_unavailable");
    }

    private static DefaultHttpContext CreateHttpContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(context.Response.Body);
        return payload.RootElement.GetProperty("error").GetString();
    }

    private sealed class FakeProbe(params AzureSqlProbeResult[] results) : IAzureSqlProbe
    {
        private readonly Queue<AzureSqlProbeResult> _results = new(results);

        public int Attempts { get; private set; }

        public Task<AzureSqlProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeReadinessService(AzureSqlReadinessResult result) : IAzureSqlReadinessService
    {
        public int Attempts { get; private set; }

        public Task<AzureSqlReadinessResult> WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(result);
        }
    }
}
