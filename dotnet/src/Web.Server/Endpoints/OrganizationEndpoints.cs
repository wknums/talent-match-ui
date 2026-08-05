using FluentValidation;
using MediatR;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Organizations;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Middleware;

namespace TalentMatch.Web.Server.Endpoints;

public static class OrganizationEndpoints
{
    public static void MapOrganizationEndpoints(this WebApplication app)
    {
        if (!string.Equals(app.Configuration["APP_AUTH_MODE"], "entra", StringComparison.OrdinalIgnoreCase))
            return;

        var group = app.MapGroup("/api/organizations")
            .WithTags("Organizations")
            .RequireAuthorization("AccessAsUser");

        group.MapGet("/", async (ISender mediator, HttpContext httpContext, CancellationToken ct) =>
            await ExecuteAsync(httpContext, null, "organization", "list", ct, async actor =>
                Results.Ok(await mediator.Send(new ListOrganizationsQuery(actor), ct))));

        group.MapPost("/", async (
            CreateOrganizationRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ExecuteAsync(httpContext, "auth.organization.changed", "organization", "new", ct, async actor =>
            {
                var result = await mediator.Send(new CreateOrganizationCommand(actor, request), ct);
                return Results.Created($"/api/organizations/{result.Id}", result);
            }));

        group.MapPost("/{organizationId}/departments", async (
            string organizationId,
            CreateDepartmentRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ExecuteAsync(httpContext, "auth.department.changed", "organization", organizationId, ct, async actor =>
            {
                var result = await mediator.Send(new CreateDepartmentCommand(actor, organizationId, request), ct);
                return Results.Created(
                    $"/api/organizations/{organizationId}/departments/{result.Id}", result);
            }));

        group.MapPatch("/{organizationId}/departments/{departmentId}", async (
            string organizationId,
            string departmentId,
            UpdateDepartmentRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ExecuteAsync(httpContext, "auth.department.changed", "department", departmentId, ct, async actor =>
                Results.Ok(await mediator.Send(
                    new UpdateDepartmentCommand(actor, organizationId, departmentId, request), ct))));

        group.MapPost("/{organizationId}/memberships", async (
            string organizationId,
            RegisterMembershipRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ExecuteAsync(httpContext, "auth.membership.activated", "user", request.UserObjectId, ct, async actor =>
                Results.Ok(await mediator.Send(
                    new RegisterOrganizationMembershipCommand(actor, organizationId, request), ct))));

        group.MapPost("/{organizationId}/role-assignments", async (
            string organizationId,
            GrantOrganizationRoleRequestDto request,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ExecuteAsync(httpContext, "auth.assignment.activated", "user", request.UserObjectId, ct, async actor =>
            {
                var result = await mediator.Send(
                    new GrantOrganizationRoleCommand(actor, organizationId, request), ct);
                return Results.Created(
                    $"/api/organizations/{organizationId}/role-assignments/{result.Id}", result);
            }));

        group.MapDelete("/{organizationId}/role-assignments/{assignmentId}", async (
            string organizationId,
            string assignmentId,
            ISender mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ExecuteAsync(httpContext, "auth.assignment.revoked", "role_assignment", assignmentId, ct, async actor =>
            {
                await mediator.Send(new RevokeOrganizationRoleCommand(actor, organizationId, assignmentId), ct);
                return Results.NoContent();
            }));
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext httpContext,
        string? validationAuditEventType,
        string validationAuditEntityType,
        string validationAuditEntityId,
        CancellationToken cancellationToken,
        Func<OrganizationActorDto, Task<IResult>> operation)
    {
        AuthorizationErrorResults.EnsureCorrelationId(httpContext);
        OrganizationActorDto? actor = null;
        try
        {
            actor = CreateActor(httpContext);
            return await operation(actor);
        }
        catch (ValidationException)
        {
            if (actor is not null && validationAuditEventType is not null)
            {
                await RecordValidationFailureAsync(
                    httpContext,
                    actor,
                    validationAuditEventType,
                    validationAuditEntityType,
                    validationAuditEntityId,
                    cancellationToken);
            }
            return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InvalidScope);
        }
        catch (OrganizationAdministrationException exception)
        {
            return AuthorizationErrorResults.Create(httpContext, exception.Code);
        }
        catch
        {
            return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.InternalError);
        }
    }

    private static async Task RecordValidationFailureAsync(
        HttpContext httpContext,
        OrganizationActorDto actor,
        string eventType,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        try
        {
            var repository = httpContext.RequestServices.GetRequiredService<IOrganizationRepository>();
            await repository.RecordFailureAuditAsync(
                new OrganizationAdministrationActor(
                    actor.TenantId,
                    actor.ObjectId,
                    actor.GlobalAdmin,
                    actor.OrganizationAdminIds,
                    actor.CorrelationId),
                eventType,
                entityType,
                entityId,
                AuthorizationErrorCodes.InvalidScope,
                cancellationToken);
        }
        catch
        {
        }
    }

    private static OrganizationActorDto CreateActor(HttpContext httpContext)
    {
        if (httpContext.Items[EntraApplicationAuthorizationMiddleware.ContextItemKey]
            is not AuthorizationContextResponse authorizationContext)
        {
            throw new OrganizationAdministrationException(
                AuthorizationErrorCodes.Forbidden,
                "Administrative access could not be verified.",
                "auth.scope.denied");
        }

        var organizationAdminIds = authorizationContext.Authorizations
            .Where(authorization =>
                authorization.Role == "organization_admin"
                && authorization.OrganizationId is not null)
            .Select(authorization => authorization.OrganizationId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new OrganizationActorDto(
            authorizationContext.TenantId,
            authorizationContext.ObjectId,
            authorizationContext.GlobalRole == "admin",
            organizationAdminIds,
            AuthorizationErrorResults.EnsureCorrelationId(httpContext));
    }
}