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

        group.MapPost("/bulk-delete", async (BulkDlqRequest request, IFailureQueueRepository repo, IApplicationRepository appRepo) =>
        {
            int deleted = 0;
            foreach (var id in request.Ids)
            {
                try
                {
                    var item = await repo.GetByIdAsync(id);
                    if (item?.EntityType == "Application")
                        await appRepo.DeleteAsync(item.EntityId);
                    await repo.RemoveAsync(id);
                    deleted++;
                }
                catch { /* item may already be gone */ }
            }
            return Results.Ok(new { deleted, total = request.Ids.Count });
        });
    }
}

public record BulkDlqRequest(List<string> Ids);
