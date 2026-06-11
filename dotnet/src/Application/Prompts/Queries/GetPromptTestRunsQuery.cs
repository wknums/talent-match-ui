using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Queries;

public record GetPromptTestRunsQuery(string PromptId) : IRequest<IReadOnlyList<PromptTestRun>>;

public class GetPromptTestRunsQueryHandler : IRequestHandler<GetPromptTestRunsQuery, IReadOnlyList<PromptTestRun>>
{
    private readonly IPromptTestRunRepository _testRunRepo;
    private readonly IApplicationRepository _applicationRepo;
    private static readonly int StaleScoringMinutes =
        int.TryParse(Environment.GetEnvironmentVariable("PROMPT_TESTRUN_SCORING_STALE_MINUTES"), out var minutes) && minutes > 0
            ? minutes
            : 10;

    public GetPromptTestRunsQueryHandler(IPromptTestRunRepository testRunRepo, IApplicationRepository applicationRepo)
    {
        _testRunRepo = testRunRepo;
        _applicationRepo = applicationRepo;
    }

    public async Task<IReadOnlyList<PromptTestRun>> Handle(GetPromptTestRunsQuery request, CancellationToken ct)
    {
        var runs = await _testRunRepo.GetByPromptIdAsync(request.PromptId, ct);

        foreach (var run in runs)
        {
            if (!IsReconcilableStatus(run.Status))
                continue;

            var applicationIds = JsonSerializer.Deserialize<List<string>>(run.ApplicationIdsJson) ?? [];
            if (applicationIds.Count == 0)
                continue;

            var (hasNonTerminal, hasFailed) = await EvaluateRunStateAsync(applicationIds, ct);
            var expectedStatus = DetermineExpectedStatus(run, hasNonTerminal, hasFailed);
            var expectedCompletedAt = string.Equals(expectedStatus, "scoring", StringComparison.OrdinalIgnoreCase)
                ? (DateTime?)null
                : (run.CompletedAt ?? DateTime.UtcNow);

            if (string.Equals(run.Status, expectedStatus, StringComparison.OrdinalIgnoreCase)
                && run.CompletedAt == expectedCompletedAt)
                continue;

            run.Status = expectedStatus;
            run.CompletedAt = expectedCompletedAt;
            await _testRunRepo.UpdateAsync(run, ct);
        }

        return runs;
    }

    private static bool IsReconcilableStatus(string? status)
        => string.Equals(status, "pending_scoring", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "scoring", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "scoring_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "pending_review", StringComparison.OrdinalIgnoreCase);

    private static string DetermineExpectedStatus(PromptTestRun run, bool hasNonTerminal, bool hasFailed)
    {
        if (hasNonTerminal)
        {
            // Timeout guard for legacy/hung runs: if still non-terminal well past expected duration,
            // stop showing endless scoring and mark as failed for operator visibility.
            return run.CreatedAt <= DateTime.UtcNow.AddMinutes(-StaleScoringMinutes)
                ? "scoring_failed"
                : "scoring";
        }

        return hasFailed ? "scoring_failed" : "pending_review";
    }

    private async Task<(bool HasNonTerminal, bool HasFailed)> EvaluateRunStateAsync(
        IReadOnlyList<string> applicationIds,
        CancellationToken ct)
    {
        var hasFailed = false;

        foreach (var appId in applicationIds)
        {
            var app = await _applicationRepo.GetByIdAsync(appId, ct);
            if (app == null)
            {
                hasFailed = true;
                continue;
            }

            if (IsStatus(app.Status, "Queued")
                || IsStatus(app.Status, "Extracting")
                || IsStatus(app.Status, "Aggregating"))
            {
                return (true, hasFailed);
            }

            if (IsStatus(app.Status, "ScoringFailed")
                || IsStatus(app.Status, "ExtractionFailed")
                || IsStatus(app.Status, "Failed"))
            {
                hasFailed = true;
                continue;
            }

            if (IsStatus(app.Status, "Scoring"))
            {
                var hasTerminalEvidence = app.FinalScore.HasValue
                    || !string.IsNullOrWhiteSpace(app.FinalDecision);

                if (!hasTerminalEvidence)
                {
                    var runsForApp = await _applicationRepo.GetScoringRunsAsync(appId, ct);
                    hasTerminalEvidence = runsForApp.Count > 0;
                }

                if (!hasTerminalEvidence)
                    return (true, hasFailed);
            }
        }

        return (false, hasFailed);
    }

    private static bool IsStatus(string? currentStatus, string expectedStatus)
        => string.Equals(currentStatus, expectedStatus, StringComparison.OrdinalIgnoreCase);
}
