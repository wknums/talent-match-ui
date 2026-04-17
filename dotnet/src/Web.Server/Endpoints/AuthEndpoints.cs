using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using TalentMatch.Application.Users.Commands;
using TalentMatch.Application.Users.Queries;
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
    }
}

public record LoginRequest(string Username, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
