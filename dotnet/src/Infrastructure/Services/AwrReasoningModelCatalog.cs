using System.Collections.Concurrent;
using System.Net.Http.Json;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.Services;

public sealed class AwrReasoningModelCatalog(HttpClient httpClient) : IReasoningModelCatalog
{
    private static readonly ConcurrentDictionary<string, CachedCatalog> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<ReasoningModelsResponse> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")?.TrimEnd('/')
            ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT is not configured.");
        if (Cache.TryGetValue(endpoint, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Value;

        using var response = await httpClient.GetAsync(
            $"{endpoint}/reasoning-models",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Reasoning model discovery failed ({(int)response.StatusCode}): {detail}");
        }

        var catalog = await response.Content.ReadFromJsonAsync<ReasoningModelsResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Reasoning model discovery returned an empty response.");
        if (string.IsNullOrWhiteSpace(catalog.DefaultModel)
            || string.IsNullOrWhiteSpace(catalog.DefaultReasoningEffort)
            || catalog.Models.Count == 0
            || catalog.SupportedReasoningEfforts.Count == 0
            || catalog.SupportedReasoningEfforts.Any(string.IsNullOrWhiteSpace)
            || !catalog.SupportedReasoningEfforts.Contains(
                catalog.DefaultReasoningEffort,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Reasoning model discovery returned an invalid contract.");
        }

        Cache[endpoint] = new CachedCatalog(
            catalog,
            DateTimeOffset.UtcNow.Add(CacheDuration));
        return catalog;
    }

    public async Task<ScoringProfile> ValidateAsync(
        string modelId,
        string reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("Model is required.");
        if (string.IsNullOrWhiteSpace(reasoningEffort))
            throw new InvalidOperationException("Reasoning effort is required.");

        var catalog = await GetAsync(cancellationToken);
        var normalizedModel = modelId.Trim();
        var normalizedReasoningEffort = reasoningEffort.Trim();
        if (!catalog.Models.Any(model =>
                string.Equals(
                    model.Deployment,
                    normalizedModel,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Model '{normalizedModel}' is not supported by the AWReason HTTP service.");
        }
        if (!catalog.SupportedReasoningEfforts.Contains(
                normalizedReasoningEffort,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reasoning effort '{normalizedReasoningEffort}' is not supported by the AWReason HTTP service.");
        }

        return new ScoringProfile(normalizedModel, normalizedReasoningEffort);
    }

    public async Task<ScoringProfile> ResolveForExecutionAsync(
        string? modelId,
        string? reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        var normalizedModel = modelId?.Trim() ?? string.Empty;
        var normalizedEffort = reasoningEffort?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedModel)
            || string.Equals(
                normalizedModel,
                "passthrough-llm",
                StringComparison.OrdinalIgnoreCase))
        {
            var catalog = await GetAsync(cancellationToken);
            return new ScoringProfile(
                catalog.DefaultModel,
                catalog.DefaultReasoningEffort);
        }

        return await ValidateAsync(
            normalizedModel,
            normalizedEffort,
            cancellationToken);
    }

    private sealed record CachedCatalog(
        ReasoningModelsResponse Value,
        DateTimeOffset ExpiresAt);
}
