namespace TalentMatch.Application.Common.Interfaces;

public interface ILlmProxyService
{
    Task<string> SendPromptAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
    Task<string> ScoreAsync(string resolvedPrompt, string candidateText, CancellationToken cancellationToken = default);
}
