using FluentValidation;
using MediatR;
using TalentMatch.Application.AccessManagement;
using TalentMatch.Application.Authorization;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Middleware;

namespace TalentMatch.Web.Server.Endpoints;

public static class AccessManagementEndpoints
{
    public static void MapAccessManagementEndpoints(this WebApplication app)
    {
        if (!string.Equals(app.Configuration["APP_AUTH_MODE"], "entra", StringComparison.OrdinalIgnoreCase))
            return;

        var group = app.MapGroup("/api/access-management")
            .WithTags("Access Management")
            .RequireAuthorization("AccessAsUser");

        group.MapGet("/users", async (
            string? search,
            string? organizationId,
            string? status,
            string? cursor,
            int? limit,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(httpContext, async actor =>
            {
                var result = await mediator.Send(new ListEntraAccessUsersQuery(
                    actor,
                    search,
                    organizationId,
                    status,
                    cursor,
                    limit ?? 25), cancellationToken);
                return Results.Ok(result);
            }));

        group.MapGet("/users/{objectId}", async (
            string objectId,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(httpContext, async actor =>
            {
                var result = await mediator.Send(
                    new GetEntraAccessUserQuery(actor, objectId),
                    cancellationToken);
                return Results.Ok(result);
            }));

        group.MapPut("/users/{objectId}/organizations/{organizationId}", async (
            string objectId,
            string organizationId,
            PutEntraOrganizationAccessRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(httpContext, async actor =>
            {
                if (request.Profile is null || request.Membership is null || request.RoleAssignments is null)
                    return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InvalidScope);

                var result = await mediator.Send(new PutEntraOrganizationAccessCommand(
                    actor,
                    objectId,
                    organizationId,
                    request), cancellationToken);
                return Results.Ok(result);
            }));

        group.MapPatch("/users/{objectId}", async (
            string objectId,
            UpdateEntraAccessUserRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(httpContext, async actor =>
            {
                var result = await mediator.Send(
                    new UpdateEntraAccessUserCommand(actor, objectId, request),
                    cancellationToken);
                return Results.Ok(result);
            }));

        group.MapDelete("/users/{objectId}/organizations/{organizationId}/role-assignments/{assignmentId}", async (
            string objectId,
            string organizationId,
            string assignmentId,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(httpContext, async actor =>
            {
                if (!TryGetExpectedVersion(httpContext, out var expectedVersion))
                    return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InvalidScope);

                var result = await mediator.Send(new RevokeEntraOrganizationRoleCommand(
                    actor,
                    objectId,
                    organizationId,
                    assignmentId,
                    expectedVersion), cancellationToken);
                return Results.Ok(result);
            }));
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext httpContext,
        Func<AccessManagementActorDto, Task<IResult>> operation)
    {
        AuthorizationErrorResults.EnsureCorrelationId(httpContext);

        try
        {
            return await operation(CreateActor(httpContext));
        }
        catch (ValidationException)
        {
            return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InvalidScope);
        }
        catch (EntraAccessManagementException exception)
        {
            return AuthorizationErrorResults.Create(httpContext, exception.Code);
        }
        catch
        {
            return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InternalError);
        }
    }

    private static AccessManagementActorDto CreateActor(HttpContext httpContext)
    {
        if (httpContext.Items[EntraApplicationAuthorizationMiddleware.ContextItemKey]
            is not AuthorizationContextResponse authorizationContext)
        {
            throw new EntraAccessManagementException(
                AuthorizationErrorCodes.Forbidden,
                "Administrative access could not be verified.");
        }

        var organizationAdminIds = authorizationContext.Authorizations
            .Where(authorization =>
                authorization.Role == "organization_admin"
                && authorization.OrganizationId is not null)
            .Select(authorization => authorization.OrganizationId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AccessManagementActorDto(
            authorizationContext.TenantId,
            authorizationContext.ObjectId,
            authorizationContext.GlobalRole == "admin",
            organizationAdminIds,
            AuthorizationErrorResults.EnsureCorrelationId(httpContext));
    }

    private static bool TryGetExpectedVersion(HttpContext httpContext, out int expectedVersion)
    {
        var rawValue = httpContext.Request.Query["expectedVersion"].ToString();
        return int.TryParse(rawValue, out expectedVersion) && expectedVersion >= 0;
    }

}