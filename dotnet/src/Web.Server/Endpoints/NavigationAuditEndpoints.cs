using System.Security.Claims;
using MediatR;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Navigation;

namespace TalentMatch.Web.Server.Endpoints;

public static class NavigationAuditEndpoints
{
    public static void MapNavigationAuditEndpoints(this WebApplication app)
    {
        app.MapPost("/api/navigation/audit", async (
            RecordNavigationAuditRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var result = await mediator.Send(
                    new RecordNavigationAuditCommand(ResolveActor(httpContext), request), ct);
                AuthorizationErrorResults.UseCorrelationId(httpContext, result.CorrelationId);
                return Results.Ok(result);
            }
            catch (NavigationAuditValidationException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InvalidScope);
            }
            catch (NavigationAuditUnavailableException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.AuditUnavailable);
            }
            catch
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InternalError);
            }
        })
        .WithTags("Navigation")
        .RequireAuthorization();
    }

    private static string ResolveActor(HttpContext httpContext)
    {
        var user = httpContext.User;
        return user.FindFirstValue("oid")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.Identity?.Name
            ?? string.Empty;
    }
}
