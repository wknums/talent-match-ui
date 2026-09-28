using MediatR;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.Stats.Queries;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Server.Endpoints;

public static class StatsEndpoints
{
    public static void MapStatsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Stats").RequireAuthorization();

        group.MapGet("/stats", async (ISender mediator) =>
            Results.Ok(await mediator.Send(new GetSystemStatsQuery())));

        group.MapGet("/stats/scoring-throughput", async (ISender mediator, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await mediator.Send(new GetScoringThroughputQuery(), ct));
        })
            .WithName("GetScoringThroughput")
            .WithSummary("Get rolling-hour application scoring throughput")
            .WithDescription("Counts unique non-test applications at their first persisted aggregate result, limited to the caller's accessible jobs. Returns 24 one-hour windows ending at asOfUtc; the final window is the rolling last 60 minutes.")
            .Produces<ScoringThroughputDto>();

        group.MapGet("/audit", async (string? entityType, string? eventType, DateTime? startDate, DateTime? endDate,
            int? page, int? pageSize, IProcessingEventRepository repo) =>
        {
            var events = await repo.GetFilteredAsync(entityType, eventType, startDate, endDate, page ?? 1, pageSize ?? 50);
            return Results.Ok(events);
        });
    }
}
