using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.Services;

public class LlmProxyService : ILlmProxyService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LlmProxyService> _logger;

    public LlmProxyService(HttpClient httpClient, IConfiguration configuration, ILogger<LlmProxyService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> SendPromptAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint not configured.");
        var apiKey = _configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey not configured.");
        var deployment = _configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4";

        var requestBody = new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.3,
            max_tokens = 4096
        };

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

        var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-02-01";

        _logger.LogInformation("Sending LLM request to {Deployment}", deployment);
        var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<LlmResponse>(cancellationToken: cancellationToken);
        return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    private record LlmResponse(List<LlmChoice>? Choices);
    private record LlmChoice(LlmMessage? Message);
    private record LlmMessage(string? Content);
}
