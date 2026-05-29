namespace TalentMatch.Application.Common.Interfaces;

public interface ILlmProxyService
{
    Task<string> SendPromptAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
    Task<string> ScoreAsync(string resolvedPrompt, string candidateText, CancellationToken cancellationToken = default);
    // FR-065/FR-066: Always uses AWR_SEQ_API_ENDPOINT. Platform-mode scoring goes through IPlatformScoringService, not this interface.
    Task<IReadOnlyList<string>> ScoreWithDocumentAsync(string resolvedPrompt, byte[] documentBytes, string fileName, string mimeType, int runs = 1, CancellationToken cancellationToken = default);
    Task<string> ExtractAsync(byte[] documentBytes, string fileName, string mimeType, CancellationToken cancellationToken = default);
}