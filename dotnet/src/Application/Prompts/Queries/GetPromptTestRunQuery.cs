using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Queries;

public record TestRunApplicationDetail(
    Domain.Entities.Application Application,
    IReadOnlyList<ScoringRun> ScoringRuns
);

public record PromptTestRunDetail(
    PromptTestRun TestRun,
    IReadOnlyList<TestRunApplicationDetail> Applications
);

public record GetPromptTestRunQuery(string TestRunId) : IRequest<PromptTestRunDetail?>;

public class GetPromptTestRunQueryHandler : IRequestHandler<GetPromptTestRunQuery, PromptTestRunDetail?>
{
    private readonly IPromptTestRunRepository _testRunRepo;
    private readonly IApplicationRepository _applicationRepo;
    private static readonly int StaleScoringMinutes =
        int.TryParse(Environment.GetEnvironmentVariable("PROMPT_TESTRUN_SCORING_STALE_MINUTES"), out var minutes) && minutes > 0
            ? minutes
            : 10;

    public GetPromptTestRunQueryHandler(
        IPromptTestRunRepository testRunRepo,
        IApplicationRepository applicationRepo)
    {
        _testRunRepo = testRunRepo;
        _applicationRepo = applicationRepo;
    }

    public async Task<PromptTestRunDetail?> Handle(GetPromptTestRunQuery request, CancellationToken ct)
    {
        var testRun = await _testRunRepo.GetByIdAsync(request.TestRunId, ct);
        if (testRun == null)
            return null;

        var applicationIds = JsonSerializer.Deserialize<List<string>>(testRun.ApplicationIdsJson) ?? [];
        var applications = new List<TestRunApplicationDetail>();

        foreach (var appId in applicationIds)
        {
            var app = await _applicationRepo.GetByIdAsync(appId, ct);
            if (app != null)
            {
                var runs = await _applicationRepo.GetScoringRunsAsync(appId, ct);
                applications.Add(new TestRunApplicationDetail(app, runs));
            }
        }

        if (IsReconcilableStatus(testRun.Status) && applicationIds.Count > 0)
        {
            var hasNonTerminal = applications.Any(a =>
            {
                var app = a.Application;
                return IsStatus(app.Status, "Queued")
                       || IsStatus(app.Status, "Extracting")
                       || IsStatus(app.Status, "Aggregating")
                       || (IsStatus(app.Status, "Scoring")
                           && !app.FinalScore.HasValue
                           && string.IsNullOrWhiteSpace(app.FinalDecision)
                           && a.ScoringRuns.Count == 0);
            });

            var hasFailed = (applications.Count != applicationIds.Count)
                            || applications.Any(a =>
                                IsStatus(a.Application.Status, "ScoringFailed")
                                || IsStatus(a.Application.Status, "ExtractionFailed")
                                || IsStatus(a.Application.Status, "Failed"));

            var expectedStatus = ResolveExpectedStatus(testRun, hasNonTerminal, hasFailed);
            var expectedCompletedAt = string.Equals(expectedStatus, "scoring", StringComparison.OrdinalIgnoreCase)
                ? (DateTime?)null
                : (testRun.CompletedAt ?? DateTime.UtcNow);

            if (!string.Equals(testRun.Status, expectedStatus, StringComparison.OrdinalIgnoreCase)
                || testRun.CompletedAt != expectedCompletedAt)
            {
                testRun.Status = expectedStatus;
                testRun.CompletedAt = expectedCompletedAt;
                await _testRunRepo.UpdateAsync(testRun, ct);
            }
        }

        return new PromptTestRunDetail(testRun, applications);
    }

    private static bool IsReconcilableStatus(string? status)
        => string.Equals(status, "pending_scoring", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "scoring", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "scoring_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "pending_review", StringComparison.OrdinalIgnoreCase);

    private static string ResolveExpectedStatus(PromptTestRun testRun, bool hasNonTerminal, bool hasFailed)
    {
        if (hasNonTerminal)
        {
            return testRun.CreatedAt <= DateTime.UtcNow.AddMinutes(-StaleScoringMinutes)
                ? "scoring_failed"
                : "scoring";
        }

        return hasFailed ? "scoring_failed" : "pending_review";
    }

    private static bool IsStatus(string? currentStatus, string expectedStatus)
        => string.Equals(currentStatus, expectedStatus, StringComparison.OrdinalIgnoreCase);
}
