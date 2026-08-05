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

        group.MapGet("/audit", async (string? entityType, string? eventType, DateTime? startDate, DateTime? endDate,
            int? page, int? pageSize, IProcessingEventRepository repo) =>
        {
            var events = await repo.GetFilteredAsync(entityType, eventType, startDate, endDate, page ?? 1, pageSize ?? 50);
            return Results.Ok(events);
        });
    }
}
