using MediatR;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public static class DlqEndpoints
{
    public static void MapDlqEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dlq").WithTags("DLQ").RequireAuthorization();

        group.MapGet("/", async (IFailureQueueRepository repo) =>
        {
            var items = await repo.GetAllAsync();
            return Results.Ok(items);
        });

        group.MapPost("/{itemId}/retry", async (string itemId, ISender mediator) =>
        {
            var result = await mediator.Send(new RetryDlqItemCommand(itemId));
            return result ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/bulk-retry", async (BulkDlqRequest request, ISender mediator) =>
        {
            int succeeded = 0;
            foreach (var id in request.Ids)
            {
                if (await mediator.Send(new RetryDlqItemCommand(id)))
                    succeeded++;
            }
            return Results.Ok(new { succeeded, total = request.Ids.Count });
        });

        group.MapPost("/bulk-delete", async (BulkDlqRequest request, IFailureQueueRepository repo, IApplicationRepository appRepo, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DlqBulkDelete");
            int deleted = 0;
            var failedIds = new List<string>();

            foreach (var id in request.Ids)
            {
                try
                {
                    var item = await repo.GetByIdAsync(id);
                    if (item?.EntityType == "Application")
                    {
                        try
                        {
                            await appRepo.DeleteAsync(item.EntityId);
                        }
                        catch (Exception ex)
                        {
                            // Do not block DLQ cleanup when app deletion fails.
                            logger.LogWarning(ex,
                                "DLQ bulk-delete: failed to delete application {ApplicationId} for item {DlqId}; continuing with DLQ removal.",
                                item.EntityId, id);
                        }
                    }

                    await repo.RemoveAsync(id);
                    deleted++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "DLQ bulk-delete: failed to remove DLQ item {DlqId}", id);
                    failedIds.Add(id);
                }
            }

            return Results.Ok(new { deleted, total = request.Ids.Count, failedIds });
        });
    }
}

public record BulkDlqRequest(List<string> Ids);
