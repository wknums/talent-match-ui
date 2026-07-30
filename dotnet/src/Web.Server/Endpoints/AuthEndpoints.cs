using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using TalentMatch.Application.Authorization;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using TalentMatch.Application.Users.Commands;
using TalentMatch.Application.Users.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");
        var useEntraAuthentication = string.Equals(
            app.Configuration["APP_AUTH_MODE"],
            "entra",
            StringComparison.OrdinalIgnoreCase);

        group.MapGet("/config", () =>
        {
            if (!useEntraAuthentication)
                return Results.Ok(new { authMode = "simple" });

            var tenantId = app.Configuration["AZURE_TENANT_ID"]!;
            var clientId = app.Configuration["ENTRA_STACK_B_CLIENT_ID"]!;
            var apiIdentifierUri = app.Configuration["ENTRA_API_IDENTIFIER_URI"]!;
            var apiScopeName = app.Configuration["ENTRA_API_SCOPE"] ?? "access_as_user";
            return Results.Ok(new
            {
                authMode = "entra",
                tenantId,
                clientId,
                authority = $"https://login.microsoftonline.com/{tenantId}",
                apiScope = $"{apiIdentifierUri.TrimEnd('/')}/{apiScopeName}",
            });
        }).AllowAnonymous();

        if (!useEntraAuthentication)
        {
            group.MapPost("/login", async (LoginRequest request, IUserRepository userRepository, HttpContext httpContext) =>
            {
                var user = await userRepository.GetByUsernameAsync(request.Username);
                if (user == null) return Results.Unauthorized();

                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Password)));
                if (user.PasswordHash != hash) return Results.Unauthorized();

                user.LastLogin = DateTime.UtcNow;
                await userRepository.UpdateAsync(user);

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, user.Id),
                    new(ClaimTypes.Name, user.Username),
                    new(ClaimTypes.Role, user.Role),
                    new("department", user.Department)
                };
                var identity = new ClaimsIdentity(claims, "cookie");
                await httpContext.SignInAsync("cookie", new ClaimsPrincipal(identity));

                return Results.Ok(new { user.Id, user.Username, user.Role, user.Department, user.FullName, user.Email });
            }).AllowAnonymous();
        }

        if (useEntraAuthentication)
        {
            group.MapGet("/me", async (
                ISender mediator,
                IUserRepository userRepository,
                IProcessingEventRepository eventRepository,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var correlationId = AddCorrelationId(httpContext);
                var result = await mediator.Send(new ResolveUserAuthorizationQuery(), cancellationToken);
                if (!result.IsSuccess)
                {
                    var error = result.Error!;
                    await eventRepository.AddAuthorizationEventAsync(
                        GetActor(httpContext),
                        ProcessingEvent.AuthorizationActions.LoginDenied,
                        GetActor(httpContext),
                        new Dictionary<string, object?>
                        {
                            ["result"] = error.Code,
                            ["tenantId"] = httpContext.User.FindFirstValue("tid"),
                        },
                        correlationId,
                        cancellationToken);
                    return Results.Json(
                        new { error = error.Code, error.Message, correlationId },
                        statusCode: error.StatusCode);
                }

                var context = result.Context!;
                await userRepository.UpdateLastLoginAsync(context.UserId, DateTime.UtcNow, cancellationToken);
                await eventRepository.AddAuthorizationEventAsync(
                    context.ObjectId,
                    ProcessingEvent.AuthorizationActions.LoginSucceeded,
                    context.UserId,
                    new Dictionary<string, object?>
                    {
                        ["result"] = "success",
                        ["tenantId"] = context.TenantId,
                        ["authorizationCount"] = context.Authorizations.Count,
                    },
                    correlationId,
                    cancellationToken);
                return Results.Ok(context);
            }).RequireAuthorization("AccessAsUser");

            group.MapPost("/logout", async (
                IProcessingEventRepository eventRepository,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var correlationId = AddCorrelationId(httpContext);
                var actor = GetActor(httpContext);
                await eventRepository.AddAuthorizationEventAsync(
                    actor,
                    ProcessingEvent.AuthorizationActions.Logout,
                    actor,
                    new Dictionary<string, object?>
                    {
                        ["result"] = "success",
                        ["tenantId"] = httpContext.User.FindFirstValue("tid"),
                    },
                    correlationId,
                    cancellationToken);
                return Results.NoContent();
            }).RequireAuthorization("AccessAsUser");
        }
        else
        {
            group.MapPost("/logout", async (HttpContext httpContext) =>
            {
                await httpContext.SignOutAsync("cookie");
                return Results.Ok();
            });

            group.MapGet("/me", async (ISender mediator) =>
            {
                var user = await mediator.Send(new GetCurrentUserQuery());
                if (user == null) return Results.Ok(null);
                return Results.Ok(new { user.Id, user.Username, user.Role, user.Department, user.FullName, user.Email });
            }).AllowAnonymous();
        }

        if (!useEntraAuthentication)
        {
            group.MapPost("/change-password", async (ChangePasswordRequest request, ISender mediator) =>
            {
                var result = await mediator.Send(new ChangePasswordCommand(request.CurrentPassword, request.NewPassword));
                return result ? Results.Ok() : Results.BadRequest("Password change failed.");
            }).RequireAuthorization();

            group.MapPost("/request-password-reset", async (RequestPasswordResetFromLoginRequest request, IUserRepository userRepository, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Username))
                    return Results.BadRequest("Username is required.");

                var username = request.Username.Trim();
                var user = await userRepository.GetByUsernameAsync(username, cancellationToken);
                if (user != null)
                {
                    var pendingRequests = await userRepository.GetResetRequestsAsync(cancellationToken);
                    var alreadyPending = pendingRequests.Any(r => r.UserId == user.Id);
                    if (!alreadyPending)
                    {
                        var resetRequest = new PasswordResetRequest
                        {
                            UserId = user.Id,
                            Username = user.Username,
                            Reason = string.IsNullOrWhiteSpace(request.Reason)
                                ? "Requested from login screen"
                                : request.Reason.Trim(),
                            Status = "pending",
                        };
                        await userRepository.AddResetRequestAsync(resetRequest, cancellationToken);
                    }
                }

                return Results.Ok(new
                {
                    message = "If the account exists, a password reset request has been submitted for admin review."
                });
            }).AllowAnonymous();
        }
    }

    private static string AddCorrelationId(HttpContext httpContext)
    {
        var correlationId = Guid.NewGuid().ToString();
        httpContext.Response.Headers["X-Correlation-ID"] = correlationId;
        return correlationId;
    }

    private static string GetActor(HttpContext httpContext)
        => httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "unknown";
}

public record LoginRequest(string Username, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record RequestPasswordResetFromLoginRequest(string Username, string? Reason);
