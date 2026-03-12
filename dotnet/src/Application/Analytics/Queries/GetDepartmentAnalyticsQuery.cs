using MediatR;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Analytics.Queries;

public record GetDepartmentAnalyticsQuery(string CallerRole, string? CallerDepartment) : IRequest<List<DepartmentAnalytics>>;

public class GetDepartmentAnalyticsQueryHandler : IRequestHandler<GetDepartmentAnalyticsQuery, List<DepartmentAnalytics>>
{
    private readonly ISender _mediator;

    public GetDepartmentAnalyticsQueryHandler(ISender mediator)
    {
        _mediator = mediator;
    }

    public async Task<List<DepartmentAnalytics>> Handle(GetDepartmentAnalyticsQuery request, CancellationToken cancellationToken)
    {
        var recruiters = await _mediator.Send(
            new GetRecruiterAnalyticsQuery(request.CallerRole, request.CallerDepartment),
            cancellationToken);

        var grouped = recruiters
            .GroupBy(r => r.Department)
            .Select(g => new DepartmentAnalytics(
                g.Key,
                g.Count(),
                g.Sum(r => r.ApplicationsInQueue),
                g.Sum(r => r.ManualReviewsPerformed),
                g.Sum(r => r.ShortlistRecommendations),
                g.Sum(r => r.ActiveJobs),
                g.ToList()))
            .ToList();

        return grouped;
    }
}
