using System.Text;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.Services;

public class LlmProxyService : ILlmProxyService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LlmProxyService> _logger;

    public LlmProxyService(HttpClient httpClient, ILogger<LlmProxyService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // FR-065: Prompt generation ALWAYS uses AWR_SEQ_API_ENDPOINT regardless of scoring mode
    public async Task<string> SendPromptAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")
            ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT is not configured.");

        var combinedPrompt = $"{systemPrompt}\n\n{userPrompt}";

        using var formData = new MultipartFormDataContent();

        var promptContent = new ByteArrayContent(Encoding.UTF8.GetBytes(combinedPrompt));
        promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(promptContent, "promptFile", "generate-prompt.md");

        // specFile with the user prompt context as a text document
        var specContent = new ByteArrayContent(Encoding.UTF8.GetBytes(userPrompt));
        specContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(specContent, "specFile", "context.md");

        _logger.LogInformation("Sending LLM request via passthrough to {Endpoint}", endpoint);
        var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Passthrough API error: {Status} {Error}", (int)response.StatusCode, errorText);
            throw new InvalidOperationException($"LLM passthrough request failed ({(int)response.StatusCode}): {errorText}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> ScoreAsync(string resolvedPrompt, string candidateText, CancellationToken cancellationToken = default)
    {
        var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")
            ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT is not configured.");

        using var formData = new MultipartFormDataContent();

        var promptContent = new ByteArrayContent(Encoding.UTF8.GetBytes(resolvedPrompt));
        promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(promptContent, "promptFile", "score-prompt.md");

        var specContent = new ByteArrayContent(Encoding.UTF8.GetBytes(candidateText));
        specContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(specContent, "specFile", "candidate-cv.md");

        _logger.LogInformation("Sending scoring request via passthrough to {Endpoint}", endpoint);
        var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Scoring passthrough API error: {Status} {Error}", (int)response.StatusCode, errorText);
            throw new InvalidOperationException($"Scoring passthrough request failed ({(int)response.StatusCode}): {errorText}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> ScoreWithDocumentAsync(string resolvedPrompt, byte[] documentBytes, string fileName, string mimeType, int runs = 1, CancellationToken cancellationToken = default, string? endpointOverride = null)
    {
        var endpoint = endpointOverride
            ?? Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")
            ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT is not configured.");

        using var formData = new MultipartFormDataContent();

        var promptContent = new ByteArrayContent(Encoding.UTF8.GetBytes(resolvedPrompt));
        promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(promptContent, "promptFile", "score-prompt.md");

        var docContent = new ByteArrayContent(documentBytes);
        docContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        formData.Add(docContent, "specFile", fileName);

        if (runs > 1)
        {
            formData.Add(new StringContent(runs.ToString()), "runs");
        }

        _logger.LogInformation("Sending scoring request with original document via passthrough to {Endpoint} for {FileName} ({MimeType}, {Size} bytes, {Runs} runs)",
            endpoint, fileName, mimeType, documentBytes.Length, runs);
        var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Scoring passthrough API error: {Status} {Error}", (int)response.StatusCode, errorText);
            throw new InvalidOperationException($"Scoring passthrough request failed ({(int)response.StatusCode}): {errorText}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    // FR-065: Extraction ALWAYS uses AWR_SEQ_API_ENDPOINT regardless of scoring mode
    public async Task<string> ExtractAsync(byte[] documentBytes, string fileName, string mimeType, int runs = 1, CancellationToken cancellationToken = default, string? endpointOverride = null)
    {
        var endpoint = endpointOverride
            ?? Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")
            ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT is not configured.");

        const string extractionPrompt = """
            You are a document extraction specialist. Extract the text content from the provided document and return it as clean, well-structured Markdown.

            Rules:
            - Preserve headings, lists, and formatting structure
            - Remove headers, footers, page numbers, and decorative elements
            - Normalise whitespace and fix OCR artefacts where obvious
            - Return ONLY the extracted Markdown text, no JSON wrapper

            Output format: raw Markdown text.
            """;

        using var formData = new MultipartFormDataContent();

        var promptContent = new ByteArrayContent(Encoding.UTF8.GetBytes(extractionPrompt));
        promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(promptContent, "promptFile", "extraction-prompt.md");

        var docContent = new ByteArrayContent(documentBytes);
        docContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        formData.Add(docContent, "specFile", fileName);

        _logger.LogInformation("Sending extraction request via passthrough to {Endpoint} for {FileName} ({MimeType}, {Size} bytes)",
            endpoint, fileName, mimeType, documentBytes.Length);
        var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Extraction passthrough API error: {Status} {Error}", (int)response.StatusCode, errorText);
            throw new InvalidOperationException($"Extraction passthrough request failed ({(int)response.StatusCode}): {errorText}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
