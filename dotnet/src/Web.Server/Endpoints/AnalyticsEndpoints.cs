using System.Security.Claims;
using MediatR;
using TalentMatch.Application.Analytics.Queries;

namespace TalentMatch.Web.Server.Endpoints;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/stats")
            .WithTags("Analytics")
            .RequireAuthorization();

        group.MapGet("/recruiters", async (HttpContext httpContext, ISender mediator) =>
        {
            var role = httpContext.User.FindFirstValue(ClaimTypes.Role);
            if (role != "admin" && role != "recruiter")
                return Results.Forbid();

            var department = httpContext.User.FindFirstValue("department");
            var result = await mediator.Send(new GetRecruiterAnalyticsQuery(role, department));
            return Results.Ok(result);
        });

        group.MapGet("/departments", async (HttpContext httpContext, ISender mediator) =>
        {
            var role = httpContext.User.FindFirstValue(ClaimTypes.Role);
            if (role != "admin" && role != "recruiter")
                return Results.Forbid();

            var department = httpContext.User.FindFirstValue("department");
            var result = await mediator.Send(new GetDepartmentAnalyticsQuery(role, department));
            return Results.Ok(result);
        });
    }
}
