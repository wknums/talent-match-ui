using FluentValidation;
using MediatR;
using TalentMatch.Application.Users.Commands;
using TalentMatch.Application.Users.Queries;

namespace TalentMatch.Web.Server.Endpoints;

public static class UsersEndpoints
{
    public static void MapUsersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (ISender mediator) =>
        {
            var users = await mediator.Send(new GetUsersQuery());
            return Results.Ok(users.Select(u => new { u.Id, u.Username, u.Role, u.Department, u.FullName, u.Email, u.CreatedAt, u.LastLogin }));
        });

        group.MapPost("/", async (CreateUserRequest request, ISender mediator) =>
        {
            try
            {
                var user = await mediator.Send(new CreateUserCommand(
                    request.Username, request.Role, request.Department, request.Password, request.FullName, request.Email));
                return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.Role, user.Department });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        group.MapDelete("/{userId}", async (string userId, ISender mediator) =>
        {
            var result = await mediator.Send(new DeleteUserCommand(userId));
            return result ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{userId}", async (string userId, UpdateUserRequest request, ISender mediator) =>
        {
            try
            {
                var result = await mediator.Send(new UpdateUserCommand(
                    userId,
                    request.FullName,
                    request.Email,
                    request.Role,
                    request.Department ?? string.Empty));

                if (!result)
                    return Results.NotFound();

                var users = await mediator.Send(new GetUsersQuery());
                var user = users.FirstOrDefault(u => u.Id == userId);

                return user == null
                    ? Results.NotFound()
                    : Results.Ok(ToUserResponse(user));
            }
            catch (ValidationException ex)
            {
                var errors = ex.Errors
                    .GroupBy(error => error.PropertyName)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(error => error.ErrorMessage).ToArray());

                return Results.ValidationProblem(errors);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        group.MapPost("/{userId}/reset-password", async (string userId, ResetPasswordRequest request, ISender mediator) =>
        {
            var result = await mediator.Send(new ResetPasswordCommand(userId, request.NewPassword));
            return result ? Results.Ok() : Results.NotFound();
        });

        // Password reset requests
        var resetGroup = app.MapGroup("/api/users/reset-requests").WithTags("Users");

        resetGroup.MapGet("/", async (ISender mediator) =>
        {
            // This needs a query - for now return from user repo
            return Results.Ok(new List<object>());
        }).RequireAuthorization("AdminOnly");

        resetGroup.MapPost("/", async (RequestResetRequest request, ISender mediator) =>
        {
            var result = await mediator.Send(new RequestPasswordResetCommand(request.Reason));
            return Results.Created($"/api/users/reset-requests/{result.Id}", result);
        }).RequireAuthorization();

        resetGroup.MapPut("/{requestId}", async (string requestId, ResolveResetRequest request, ISender mediator) =>
        {
            var result = await mediator.Send(new ResolveResetRequestCommand(requestId, request.Action, request.NewPassword));
            return result ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization("AdminOnly");
    }

    private static object ToUserResponse(TalentMatch.Domain.Entities.User user) => new
    {
        user.Id,
        user.Username,
        user.Role,
        user.Department,
        user.FullName,
        user.Email,
        user.CreatedAt,
        user.LastLogin,
    };
}

public record CreateUserRequest(string Username, string Role, string Department, string Password, string FullName, string Email);
public record UpdateUserRequest(string FullName, string Email, string Role, string? Department);
public record ResetPasswordRequest(string NewPassword);
public record RequestResetRequest(string Reason);
public record ResolveResetRequest(string Action, string? NewPassword);
