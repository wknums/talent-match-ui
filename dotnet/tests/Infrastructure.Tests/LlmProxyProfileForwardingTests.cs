using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure.Services;

namespace TalentMatch.Infrastructure.Tests;

public sealed class LlmProxyProfileForwardingTests
{
    [Fact]
    public void Profile_provider_uses_conventional_names_and_defaults()
    {
        ConfigurationScoringProfileProvider.DefaultModelId.Should().Be("passthrough-llm");
        ConfigurationScoringProfileProvider.DefaultReasoningLevel.Should().Be("medium");

        var configured = new ConfigurationScoringProfileProvider(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AWR_MODEL_ID"] = "configured-model",
                    ["AWR_REASONING_LEVEL"] = "low",
                })
                .Build());
        configured.Current.Should().Be(new ScoringProfile("configured-model", "low"));
    }

    [Fact]
    public async Task Scoring_multipart_forwards_model_and_reasoning()
    {
        var previous = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
        Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", "https://awr.example");
        try
        {
            var handler = new CapturingHandler();
            var service = new LlmProxyService(
                new HttpClient(handler),
                NullLogger<LlmProxyService>.Instance,
                new FixedProfileProvider(new ScoringProfile("model-42", "high")));

            await service.ScoreWithDocumentAsync(
                "score prompt", [1, 2, 3], "candidate.pdf", "application/pdf");

            handler.RequestBody.Should().Contain("name=reasoningModel");
            handler.RequestBody.Should().Contain("model-42");
            handler.RequestBody.Should().Contain("name=reasoningEffort");
            handler.RequestBody.Should().Contain("high");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", previous);
        }
    }

    [Fact]
    public async Task Scoring_failure_surfaces_safe_problem_details()
    {
        var previous = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
        Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", "https://awr.example");
        try
        {
            var handler = new CapturingHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """
                        {"type":"about:blank","title":"Invalid reasoningModel","status":400,"detail":"Reasoning model 'legacy' is not configured.","instance":"/assess/passthrough","correlationId":"correlation-1"}
                        """)
                }
            };
            var service = new LlmProxyService(
                new HttpClient(handler),
                NullLogger<LlmProxyService>.Instance,
                new FixedProfileProvider(new ScoringProfile("legacy", "medium")));

            var action = () => service.ScoreWithDocumentAsync(
                "score prompt",
                [1, 2, 3],
                "candidate.pdf",
                "application/pdf");

            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Invalid reasoningModel*not configured*Correlation ID: correlation-1*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", previous);
        }
    }

    [Fact]
    public async Task Compatibility_overload_resolves_legacy_profile_before_sending()
    {
        var previous = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
        Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", "https://awr.example");
        try
        {
            var handler = new CapturingHandler();
            var catalog = new MockReasoningModelCatalog(
                new ScoringProfile("gpt-5.6-luna", "high"));
            var service = new LlmProxyService(
                new HttpClient(handler),
                NullLogger<LlmProxyService>.Instance,
                new FixedProfileProvider(new ScoringProfile("passthrough-llm", "medium")),
                catalog);

            await service.ScoreWithDocumentAsync(
                "score prompt",
                [1, 2, 3],
                "candidate.pdf",
                "application/pdf");

            handler.RequestBody.Should().Contain("gpt-5.6-luna");
            handler.RequestBody.Should().Contain("high");
            handler.RequestBody.Should().NotContain("passthrough-llm");
            catalog.ResolveCalls.Should().Be(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", previous);
        }
    }

    private sealed class FixedProfileProvider(ScoringProfile profile)
        : IScoringProfileProvider
    {
        public ScoringProfile Current { get; } = profile;
    }

    private sealed class MockReasoningModelCatalog(ScoringProfile resolved)
        : IReasoningModelCatalog
    {
        public int ResolveCalls { get; private set; }

        public Task<ReasoningModelsResponse> GetAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ScoringProfile> ResolveForExecutionAsync(
            string? modelId,
            string? reasoningEffort,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Task.FromResult(resolved);
        }

        public Task<ScoringProfile> ValidateAsync(
            string modelId,
            string reasoningEffort,
            CancellationToken cancellationToken = default)
            => Task.FromResult(resolved);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;
        public HttpResponseMessage? Response { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }
    }
}
