using System.Text;
using TalentMatch.Application.JobExtraction.Services;

namespace TalentMatch.Infrastructure.Services;

public sealed class AwrJobSpecExtractionService : IJobSpecExtractionTransport
{
    private readonly HttpClient _httpClient;

    public AwrJobSpecExtractionService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> ExtractAsync(
        byte[] documentBytes,
        string fileName,
        string mimeType,
        string prompt,
        CancellationToken cancellationToken = default)
        => await ExtractAsync(
            documentBytes,
            fileName,
            mimeType,
            prompt,
            Environment.GetEnvironmentVariable("AWR_MODEL_ID") ?? "o3",
            Environment.GetEnvironmentVariable("AWR_REASONING_LEVEL") ?? "high",
            cancellationToken);

    public async Task<string> ExtractAsync(
        byte[] documentBytes,
        string fileName,
        string mimeType,
        string prompt,
        string modelId,
        string reasoningLevel,
        CancellationToken cancellationToken = default)
    {
        var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT")
            ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT is not configured.");

        using var formData = new MultipartFormDataContent();

        var promptContent = new ByteArrayContent(Encoding.UTF8.GetBytes(prompt));
        promptContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formData.Add(promptContent, "promptFile", "job-spec-extraction-prompt.md");

        var docContent = new ByteArrayContent(documentBytes);
        docContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType)
            ? "application/octet-stream"
            : mimeType);
        formData.Add(docContent, "specFile", fileName);
        formData.Add(new StringContent(modelId), "reasoningModel");
        formData.Add(new StringContent(reasoningLevel), "reasoningEffort");

        var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Job specification extraction failed ({(int)response.StatusCode}): {errorText}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
