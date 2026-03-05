using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Server.Endpoints;

public static class StatsEndpoints
{
    public static void MapStatsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Stats").RequireAuthorization();

        group.MapGet("/stats", async (AppDbContext db) =>
        {
            var stats = new
            {
                Queued = await db.Applications.CountAsync(a => a.Status == "Queued"),
                Extracting = await db.Applications.CountAsync(a => a.Status == "Extracting"),
                Scoring = await db.Applications.CountAsync(a => a.Status == "Scoring"),
                Aggregating = await db.Applications.CountAsync(a => a.Status == "Aggregating"),
                Completed = await db.Applications.CountAsync(a => a.Status == "Completed"),
                NeedsManualReview = await db.Applications.CountAsync(a => a.Status == "NeedsManualReview"),
                Failed = await db.Applications.CountAsync(a => a.Status == "Failed"),
                TotalJobs = await db.Jobs.CountAsync(),
                TotalApplications = await db.Applications.CountAsync()
            };
            return Results.Ok(stats);
        });

        group.MapGet("/audit", async (string? entityType, string? eventType, DateTime? startDate, DateTime? endDate,
            int? page, int? pageSize, IProcessingEventRepository repo) =>
        {
            var events = await repo.GetFilteredAsync(entityType, eventType, startDate, endDate, page ?? 1, pageSize ?? 50);
            return Results.Ok(events);
        });
    }
}
