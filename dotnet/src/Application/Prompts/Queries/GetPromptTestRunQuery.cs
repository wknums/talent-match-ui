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

        return new PromptTestRunDetail(testRun, applications);
    }
}
