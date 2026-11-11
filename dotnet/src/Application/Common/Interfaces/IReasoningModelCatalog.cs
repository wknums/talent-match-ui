namespace TalentMatch.Application.Common.Interfaces;

public sealed record ReasoningModelOption(
    string Slot,
    string Deployment,
    bool IsDefault);

public sealed record ReasoningModelsResponse(
    string DefaultModel,
    string DefaultReasoningEffort,
    IReadOnlyList<string> SupportedReasoningEfforts,
    IReadOnlyList<ReasoningModelOption> Models);

public interface IReasoningModelCatalog
{
    Task<ReasoningModelsResponse> GetAsync(CancellationToken cancellationToken = default);
    Task<ScoringProfile> ResolveForExecutionAsync(
        string? modelId,
        string? reasoningEffort,
        CancellationToken cancellationToken = default);
    Task<ScoringProfile> ValidateAsync(
        string modelId,
        string reasoningEffort,
        CancellationToken cancellationToken = default);
}
