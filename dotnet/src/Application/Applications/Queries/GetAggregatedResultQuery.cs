using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Services;
using System.Text.Json;

namespace TalentMatch.Application.Applications.Queries;

public record GetAggregatedResultQuery(string ApplicationId) : IRequest<AggregatedResult?>;

public class GetAggregatedResultQueryHandler : IRequestHandler<GetAggregatedResultQuery, AggregatedResult?>
{
    private readonly IApplicationRepository _applicationRepository;

    public GetAggregatedResultQueryHandler(IApplicationRepository applicationRepository)
    {
        _applicationRepository = applicationRepository;
    }

    public async Task<AggregatedResult?> Handle(GetAggregatedResultQuery request, CancellationToken cancellationToken)
    {
        var result = await _applicationRepository.GetAggregatedResultAsync(
            request.ApplicationId, cancellationToken);
        if (result is null) return null;

        result.FinalScore = ScorePrecision.Round(result.FinalScore);
        result.Variance = ScorePrecision.Round(result.Variance);
        result.Confidence = ScorePrecision.Round(result.Confidence);
        try
        {
            var subScores = JsonSerializer.Deserialize<Dictionary<string, double>>(
                result.FinalSubScoresJson);
            if (subScores is not null)
            {
                result.FinalSubScoresJson = JsonSerializer.Serialize(
                    subScores.ToDictionary(
                        item => item.Key,
                        item => ScorePrecision.Round(item.Value)));
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
