using MediatR;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using TalentMatch.Application.Authorization;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Endpoints;

namespace TalentMatch.Web.Server.Middleware;

public sealed class EntraApplicationAuthorizationMiddleware(RequestDelegate next)
{
    public const string ContextItemKey = "TalentMatch.AuthorizationContext";

    /// <summary>Keys the authentication stage uses to record why it rejected a token.</summary>
    public const string AuthenticationErrorItemKey = "AuthErrorCode";
    public const string AuthenticationActorItemKey = "AuthActor";
    public const string AuthenticationTenantItemKey = "AuthTenantId";

    public async Task InvokeAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        ISender mediator,
        IProcessingEventRepository eventRepository)
    {
        if (!string.Equals(configuration["APP_AUTH_MODE"], "entra", StringComparison.OrdinalIgnoreCase))
        {
            await next(httpContext);
            return;
        }

        var endpoint = httpContext.GetEndpoint();
        var requiresAuthorization = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
        var isAuthenticatedApiRequest = httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.Request.Path.StartsWithSegments("/api");
        var allowsAnonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var isAuthenticationOperation = httpContext.Request.Path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)
            || httpContext.Request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
        var isCandidateVerification = endpoint?.Metadata.GetMetadata<AdministrationCandidateEndpoint>() is not null
            && HttpMethods.IsPost(httpContext.Request.Method)
            && httpContext.Request.Path.Equals("/api/auth/admin-transition/verify", StringComparison.OrdinalIgnoreCase);

        if ((!requiresAuthorization && !isAuthenticatedApiRequest) || allowsAnonymous || isAuthenticationOperation || isCandidateVerification)
        {
            await next(httpContext);
            return;
        }

        AuthorizationErrorResults.EnsureCorrelationId(httpContext);
        var result = await mediator.Send(new ResolveUserAuthorizationQuery(), httpContext.RequestAborted);
        if (result.IsSuccess)
        {
            httpContext.Items[ContextItemKey] = result.Context;
            // Literal compatibility role checks see only current resolved authority,
            // never the stale JWT Admin claim after removal or a committed cutover.
            if (httpContext.User.Identity is ClaimsIdentity authenticatedIdentity)
            {
                var projected = new ClaimsIdentity(authenticatedIdentity);
                foreach (var roleClaim in projected.Claims.Where(claim =>
                    claim.Type == "roles" || claim.Type == ClaimTypes.Role || claim.Type == projected.RoleClaimType).ToArray())
                    projected.RemoveClaim(roleClaim);
                foreach (var role in result.Context!.Authorizations.Select(x => x.Role).Distinct())
                    projected.AddClaim(new Claim(projected.RoleClaimType, role));
                httpContext.User = new ClaimsPrincipal(projected);
            }
            await next(httpContext);
            return;
        }

        var correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext);

        // This middleware runs between authentication and authorization, so it also sees requests
        // whose token authentication already rejected. Resolving authorization for those can only
        // ever report the generic "sign in required", which hides the real reason and, because the
        // browser keys its silent refresh on the "token_stale" code, silently disables session
        // recovery. Report what authentication decided whenever it left a verdict behind.
        var authenticationError = httpContext.User.Identity?.IsAuthenticated == true
            ? null
            : httpContext.Items[AuthenticationErrorItemKey] as string;
        var errorCode = authenticationError ?? result.Error!.Code;
        var actor = httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Items[AuthenticationActorItemKey] as string
            ?? "unknown";
        var tenantId = httpContext.User.FindFirstValue("tid")
            ?? httpContext.Items[AuthenticationTenantItemKey] as string;
        if (result.PendingProfileCreated)
        {
            await eventRepository.AddAuthorizationEventAsync(
                actor,
                ProcessingEvent.AuthorizationActions.ProfilePending,
                actor,
                new Dictionary<string, object?>
                {
                    ["result"] = "pending",
                    ["tenantId"] = tenantId,
                },
                correlationId,
                httpContext.RequestAborted);
        }

        await eventRepository.AddAuthorizationEventAsync(
            actor,
            errorCode == AuthorizationErrorCodes.TokenStale
                ? ProcessingEvent.AuthorizationActions.TokenStale
                : ProcessingEvent.AuthorizationActions.LoginDenied,
            actor,
            AdministrationAuditDetails.WithCurrent(httpContext, new Dictionary<string, object?>
            {
                ["result"] = errorCode,
                ["tenantId"] = tenantId,
            }),
            correlationId,
            httpContext.RequestAborted);

        await AuthorizationErrorResults.WriteAsync(
            httpContext,
            errorCode,
            httpContext.RequestAborted);
    }
}