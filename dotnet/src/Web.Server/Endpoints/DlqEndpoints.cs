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
    }
}
