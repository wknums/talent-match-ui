using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

public record LoginRequest(string Username, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record RequestPasswordResetFromLoginRequest(string Username, string? Reason);
