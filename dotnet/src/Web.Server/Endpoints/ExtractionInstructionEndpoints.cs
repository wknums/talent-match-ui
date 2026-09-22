using MediatR;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.ExtractionInstructions.Commands;
using TalentMatch.Application.ExtractionInstructions.Models;
using TalentMatch.Application.ExtractionInstructions.Queries;

namespace TalentMatch.Web.Server.Endpoints;

public static class ExtractionInstructionEndpoints
{
    public static void MapExtractionInstructionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/extraction-instructions")
            .WithTags("ExtractionInstructions")
            .RequireAuthorization();

        group.AddEndpointFilter((context, next) =>
        {
            if (context.HttpContext.User.Identity?.IsAuthenticated != true)
                return ValueTask.FromResult<object?>(AuthorizationErrorResults.Create(
                    context.HttpContext,
                    AuthorizationErrorCodes.AuthRequired));

            if (!context.HttpContext.User.IsInRole("admin"))
                return ValueTask.FromResult<object?>(AuthorizationErrorResults.Create(
                    context.HttpContext,
                    AuthorizationErrorCodes.Forbidden));

            return next(context);
        });

        group.MapGet("/", async (ISender mediator)
            => Results.Ok(await mediator.Send(new GetExtractionInstructionsQuery())));

        group.MapGet("/{versionId}", async (string versionId, ISender mediator, HttpContext httpContext) =>
        {
            var version = await mediator.Send(new GetExtractionInstructionQuery(versionId));
            return version is null
                ? AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound)
                : Results.Ok(version);
        });

        group.MapPost("/", async (CreateExtractionInstructionDraftRequest request, ISender mediator, HttpContext httpContext) =>
        {
            try
            {
                var created = await mediator.Send(new CreateExtractionInstructionCommand(
                    request.InstructionText,
                    request.ChangeNote,
                    request.ModelId,
                    request.ReasoningLevel));
                return Results.Created($"/api/admin/extraction-instructions/{created.Id}", created);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.Forbidden);
            }
        });

        group.MapPost("/{versionId}/validate", async (string versionId, ExtractDocumentRequest request, ISender mediator, HttpContext httpContext) =>
        {
            try
            {
                var result = await mediator.Send(new ValidateExtractionInstructionCommand(versionId, request.FileName, request.Content, request.MimeType));
                return result.IsValid
                    ? Results.Ok(result)
                    : Results.Ok(new
                    {
                        result.ExtractionId,
                        result.InstructionVersionId,
                        result.ProtectedContractVersion,
                        result.ValidationStatus,
                        result.ValidationFindings,
                        correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                    });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
            catch (InvalidOperationException)
            {
                return Results.Json(new
                {
                    error = "validation_failed",
                    message = "AWReason validation request failed.",
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapPost("/{versionId}/activate", async (string versionId, ActivateExtractionInstructionRequest request, ISender mediator, HttpContext httpContext) =>
        {
            try
            {
                var activated = await mediator.Send(new ActivateExtractionInstructionCommand(versionId, request.ExpectedConcurrencyVersion));
                return Results.Ok(activated);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("stale_version", StringComparison.Ordinal))
            {
                return Results.Json(new
                {
                    error = "stale_version",
                    message = ex.Message,
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "validation_failed",
                    message = ex.Message,
                    correlationId = AuthorizationErrorResults.EnsureCorrelationId(httpContext),
                }, statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }
}
