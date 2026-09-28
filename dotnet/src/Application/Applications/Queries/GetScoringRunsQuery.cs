using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Services;
using System.Text.Json;

namespace TalentMatch.Application.Applications.Queries;

public record GetScoringRunsQuery(string ApplicationId) : IRequest<IReadOnlyList<ScoringRun>>;

public class GetScoringRunsQueryHandler : IRequestHandler<GetScoringRunsQuery, IReadOnlyList<ScoringRun>>
{
    private readonly IApplicationRepository _applicationRepository;

    public GetScoringRunsQueryHandler(IApplicationRepository applicationRepository)
    {
        _applicationRepository = applicationRepository;
    }

    public async Task<IReadOnlyList<ScoringRun>> Handle(GetScoringRunsQuery request, CancellationToken cancellationToken)
    {
        var runs = await _applicationRepository.GetScoringRunsAsync(
            request.ApplicationId, cancellationToken);
        foreach (var run in runs)
        {
            run.TotalScore = ScorePrecision.Round(run.TotalScore);
            try
            {
                var categoryScores = JsonSerializer.Deserialize<Dictionary<string, double>>(
                    run.CategoryScoresJson);
                if (categoryScores is not null)
                {
                    run.CategoryScoresJson = JsonSerializer.Serialize(
                        categoryScores.ToDictionary(
                            item => item.Key,
                            item => ScorePrecision.Round(item.Value)));
                }
            }
            catch (JsonException)
            {
            }
        }
        return runs;
    }
}
