using MediatR;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Uploads.Commands;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Queries;

namespace TalentMatch.Web.Server.Endpoints;

public static class UploadSettingsEndpoints
{
    public static void MapUploadSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/upload-settings")
            .WithTags("Upload settings")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/", async (ISender sender, HttpContext context, CancellationToken ct) =>
        {
            var result = await sender.Send(new GetUploadSettingsQuery(), ct);
            context.Response.Headers["X-Correlation-ID"] = UploadEndpoints.ResolveCorrelationId(context);
            return Results.Ok(result);
        });

        group.MapPut("/", async (
            UpdateUploadSettingsRequest request,
            ISender sender,
            HttpContext context,
            CancellationToken ct) =>
        {
            try
            {
                var result = await sender.Send(
                    new UpdateUploadSettingsCommand(
                        request, UploadEndpoints.ResolveCorrelationId(context)), ct);
                return Results.Ok(result);
            }
            catch (UploadValidationException ex)
            {
                return UploadEndpoints.Validation(context, ex.Errors);
            }
            catch (InvalidOperationException ex) when (ex.Message == "stale_version")
            {
                return UploadEndpoints.ApiError(
                    context, 409, "stale_version", "The upload settings have changed.");
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(context, AuthorizationErrorCodes.Forbidden);
            }
        });
    }
}
