using System.Text;
using System.Text.Json;
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
            throw new InvalidOperationException(BuildPassthroughFailureMessage((int)response.StatusCode, "LLM passthrough request", errorText));
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
        formData.Add(specContent, "cvFiles[]", "candidate-cv.md");

        _logger.LogInformation("Sending scoring request via passthrough to {Endpoint}", endpoint);
        var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Scoring passthrough API error: {Status} {Error}", (int)response.StatusCode, errorText);
            throw new InvalidOperationException(BuildPassthroughFailureMessage((int)response.StatusCode, "Scoring passthrough request", errorText));
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private const int MaxRetriesPerRun = 3;

    private static string InferMimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        _ => "application/octet-stream"
    };

    // FR-065/FR-066: Always uses AWR_SEQ_API_ENDPOINT — platform-mode scoring lives in IPlatformScoringService.
    public async Task<IReadOnlyList<string>> ScoreWithDocumentAsync(string resolvedPrompt, byte[] documentBytes, string fileName, string mimeType, int runs = 1, CancellationToken cancellationToken = default)
    {
        var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")
            ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT is not configured.");

        // Passthrough is single-run-per-call. Loop N times with batch params (matching Stack A pattern).
        var batchId = runs > 1 ? Guid.NewGuid().ToString() : null;
        var results = new List<string>(runs);

        for (int runNumber = 1; runNumber <= runs; runNumber++)
        {
            var responseText = await CallEngineOnceWithRetry(
                endpoint, resolvedPrompt, documentBytes, fileName, mimeType,
                batchId, runNumber, runs, cancellationToken);
            results.Add(responseText);
        }

        return results;
    }

    private async Task<string> CallEngineOnceWithRetry(
        string endpoint, string resolvedPrompt, byte[] documentBytes, string fileName, string mimeType,
        string? batchId, int runNumber, int totalRuns, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxRetriesPerRun; attempt++)
        {
            try
            {
                return await CallEngineOnce(endpoint, resolvedPrompt, documentBytes, fileName, mimeType,
                    batchId, runNumber, totalRuns, cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxRetriesPerRun)
            {
                _logger.LogWarning(ex, "Scoring run {RunNumber} attempt {Attempt}/{MaxRetries} failed, retrying...",
                    runNumber, attempt, MaxRetriesPerRun);
            }
        }

        // Final attempt — let it throw
        return await CallEngineOnce(endpoint, resolvedPrompt, documentBytes, fileName, mimeType,
            batchId, runNumber, totalRuns, cancellationToken);
    }

    private async Task<string> CallEngineOnce(
        string endpoint, string resolvedPrompt, byte[] documentBytes, string fileName, string mimeType,
        string? batchId, int runNumber, int totalRuns, CancellationToken cancellationToken)
    {
        using var formData = new MultipartFormDataContent();

        var promptContent = new ByteArrayContent(Encoding.UTF8.GetBytes(resolvedPrompt));
        promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(promptContent, "promptFile", "score-prompt.md");

        var docContent = new ByteArrayContent(documentBytes);
        var resolvedMimeType = string.IsNullOrWhiteSpace(mimeType) ? InferMimeType(fileName) : mimeType;
        docContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(resolvedMimeType);
        formData.Add(docContent, "cvFiles[]", fileName);

        // Batch params for multi-run coordination (engine uses these for variance analysis)
        if (batchId != null)
        {
            formData.Add(new StringContent(batchId), "batchId");
            formData.Add(new StringContent(runNumber.ToString()), "runNumber");
            formData.Add(new StringContent(totalRuns.ToString()), "totalRuns");
        }

        _logger.LogInformation("Sending scoring run {RunNumber}/{TotalRuns} via passthrough to {Endpoint} for {FileName} ({MimeType}, {Size} bytes, batchId={BatchId})",
            runNumber, totalRuns, endpoint, fileName, mimeType, documentBytes.Length, batchId ?? "none");
        var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Scoring passthrough API error on run {RunNumber}: {Status} {Error}", runNumber, (int)response.StatusCode, errorText);
            throw new InvalidOperationException(BuildPassthroughFailureMessage((int)response.StatusCode, "Scoring passthrough request", errorText));
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    // FR-065: Extraction ALWAYS uses AWR_SEQ_API_ENDPOINT regardless of scoring mode
    public async Task<string> ExtractAsync(byte[] documentBytes, string fileName, string mimeType, CancellationToken cancellationToken = default)
    {
        var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")
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
            throw new InvalidOperationException(BuildPassthroughFailureMessage((int)response.StatusCode, "Extraction passthrough request", errorText));
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string BuildPassthroughFailureMessage(int statusCode, string operation, string errorText)
    {
        // Surface a concise, actionable diagnostic for the most common Azure OpenAI misconfiguration.
        if (errorText.Contains("DeploymentNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operation} failed ({statusCode}): Azure OpenAI deployment not found in AWReason service (DeploymentNotFound). Verify awreason-http-service deployment env vars (AOAI_DEPLOYMENT/AZURE_OPENAI_DEPLOYMENT) and API version.";
        }

        if (TryReadProblemDetails(errorText, out var title, out var detail))
        {
            var condensed = string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}";
            return $"{operation} failed ({statusCode}): {Truncate(condensed, 600)}";
        }

        return $"{operation} failed ({statusCode}): {Truncate(errorText, 600)}";
    }

    private static bool TryReadProblemDetails(string json, out string title, out string detail)
    {
        title = string.Empty;
        detail = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
            {
                title = titleProp.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("detail", out var detailProp) && detailProp.ValueKind == JsonValueKind.String)
            {
                detail = detailProp.GetString() ?? string.Empty;
            }

            return !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(detail);
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "...";
    }
}
