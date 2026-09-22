using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Services;

public sealed record PromptProfileStatus(
    string PromptId,
    string ModelId,
    string ReasoningLevel,
    string CurrentModelId,
    string CurrentReasoningLevel,
    bool IsMatch,
    bool HasExactProfileApprovedTest,
    string? ApprovedTestRunId,
    string? MismatchMessage);

public interface IPromptProfileGuard
{
    Task<PromptProfileStatus> GetStatusAsync(
        ScoringPrompt prompt, CancellationToken cancellationToken = default);
    Task EnsureProductionReadyAsync(
        ScoringPrompt prompt, CancellationToken cancellationToken = default);
}

public sealed class PromptProfileGuard : IPromptProfileGuard
{
    private readonly IPromptTestRunRepository testRuns;

    public PromptProfileGuard(IPromptTestRunRepository testRuns)
    {
        this.testRuns = testRuns;
    }

    public PromptProfileGuard(
        IScoringProfileProvider profileProvider,
        IPromptTestRunRepository testRuns)
        : this(testRuns)
    {
        ArgumentNullException.ThrowIfNull(profileProvider);
    }

    public async Task<PromptProfileStatus> GetStatusAsync(
        ScoringPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        var selected = new ScoringProfile(prompt.ModelId, prompt.ReasoningLevel);
        var runs = await testRuns.GetByPromptIdAsync(prompt.Id, cancellationToken);
        var exactApproved = runs
            .Where(run => run.Status == "approved"
                          && selected.Matches(run.ModelId, run.ReasoningLevel)
                          && selected.Matches(run.ApprovedModelId, run.ApprovedReasoningLevel))
            .OrderByDescending(run => run.CompletedAt)
            .FirstOrDefault();
        var approvalMatches = selected.Matches(
            prompt.ApprovedModelId, prompt.ApprovedReasoningLevel);
        var isMatch = !string.IsNullOrWhiteSpace(selected.ModelId)
                      && !string.IsNullOrWhiteSpace(selected.ReasoningLevel)
                      && approvalMatches
                      && exactApproved is not null;
        var mismatch = isMatch
            ? null
            : $"Production scoring is blocked. Prompt v{prompt.VersionNumber} is configured for "
              + $"'{selected.ModelId}' / '{selected.ReasoningLevel}', but it has not been tested "
              + "and approved for that exact profile. Create a new prompt version if the selection changes.";

        return new PromptProfileStatus(
            prompt.Id,
            prompt.ApprovedModelId ?? prompt.ModelId,
            prompt.ApprovedReasoningLevel ?? prompt.ReasoningLevel,
            selected.ModelId,
            selected.ReasoningLevel,
            isMatch,
            exactApproved is not null,
            exactApproved?.Id,
            mismatch);
    }

    public async Task EnsureProductionReadyAsync(
        ScoringPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(prompt, cancellationToken);
        if (!status.IsMatch)
            throw new ScoringProfileMismatchException(status.MismatchMessage!);
    }
}

public sealed class ScoringProfileMismatchException(string message)
    : InvalidOperationException(message);
