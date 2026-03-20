namespace TalentMatch.Application.Common.Interfaces;

public interface ILlmProxyService
{
    Task<string> SendPromptAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
    Task<string> ScoreAsync(string resolvedPrompt, string candidateText, CancellationToken cancellationToken = default);
    Task<string> ScoreWithDocumentAsync(string resolvedPrompt, byte[] documentBytes, string fileName, string mimeType, int runs = 1, CancellationToken cancellationToken = default, string? endpointOverride = null);
    Task<string> ExtractAsync(byte[] documentBytes, string fileName, string mimeType, CancellationToken cancellationToken = default);
}