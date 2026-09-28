using MediatR;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Uploads.Commands;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Queries;

namespace TalentMatch.Web.Server.Endpoints;

public static class UploadEndpoints
{
    public static void MapUploadEndpoints(this WebApplication app)
    {
        var jobGroup = app.MapGroup("/api/jobs/{jobId}")
            .WithTags("Upload sessions")
            .RequireAuthorization();

        jobGroup.MapPost("/upload-sessions", async (
            string jobId,
            CreateUploadSessionRequest request,
            HttpContext httpContext,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var correlationId = ResolveCorrelationId(httpContext);
            try
            {
                var created = await sender.Send(
                    new CreateUploadSessionCommand(jobId, request, correlationId),
                    cancellationToken);
                httpContext.Response.Headers["X-Correlation-ID"] = correlationId;
                return Results.Created($"/api/upload-sessions/{created.Id}", created);
            }
            catch (UploadValidationException ex)
            {
                return Validation(httpContext, ex.Errors);
            }
            catch (KeyNotFoundException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.Forbidden);
            }
        });

        var sessions = app.MapGroup("/api/upload-sessions")
            .WithTags("Upload sessions")
            .RequireAuthorization();

        sessions.MapGet("/", async (
            string? jobId,
            bool? includeTerminal,
            ISender sender,
            CancellationToken cancellationToken) =>
            Results.Ok(await sender.Send(
                new ListOwnedUploadSessionsQuery(jobId, includeTerminal ?? true),
                cancellationToken)));

        sessions.MapGet("/{sessionId}", async (
            string sessionId,
            HttpContext httpContext,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await sender.Send(
                    new GetUploadSessionQuery(sessionId), cancellationToken);
                return result is null
                    ? AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound)
                    : Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.Forbidden);
            }
            catch (KeyNotFoundException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
        });

        sessions.MapPost("/{sessionId}/heartbeat", async (
            string sessionId,
            HeartbeatRequest request,
            HttpContext httpContext,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sender.Send(
                    new HeartbeatUploadSessionCommand(
                        sessionId,
                        request.ExpectedConcurrencyVersion,
                        ResolveCorrelationId(httpContext)),
                    cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.Forbidden);
            }
            catch (InvalidOperationException ex) when (ex.Message == "stale_version")
            {
                return ApiError(httpContext, 409, "stale_version", "The upload session has changed.");
            }
        });

        sessions.MapPatch("/{sessionId}/items/{itemId}/status", async (
            string sessionId,
            string itemId,
            UpdateUploadItemStatusRequest request,
            HttpContext httpContext,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sender.Send(
                    new UpdateUploadItemStatusCommand(
                        sessionId, itemId, request, ResolveCorrelationId(httpContext)),
                    cancellationToken));
            }
            catch (UploadValidationException ex)
            {
                return Validation(httpContext, ex.Errors);
            }
            catch (KeyNotFoundException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.Forbidden);
            }
            catch (InvalidOperationException ex) when (
                ex.Message is "stale_version" or "occurrence_key_mismatch"
                    || ex.Message.StartsWith("Illegal upload item transition", StringComparison.Ordinal))
            {
                return ApiError(httpContext, 409, "state_conflict", ex.Message);
            }
        });

        sessions.MapPost("/{sessionId}/items/{itemId}/content", async (
            string sessionId,
            string itemId,
            HttpContext httpContext,
            ISender sender,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            IFormCollection form;
            try
            {
                form = await httpContext.Request.ReadFormAsync(cancellationToken);
            }
            catch (InvalidDataException)
            {
                return ApiError(httpContext, 400, "invalid_multipart", "Exactly one file is required.");
            }
            if (form.Files.Count != 1)
                return ApiError(httpContext, 400, "invalid_multipart", "Exactly one file is required.");
            if (!Guid.TryParse(httpContext.Request.Headers["Idempotency-Key"], out var occurrenceKey))
                return ApiError(httpContext, 400, "invalid_idempotency_key", "A UUID Idempotency-Key is required.");

            var file = form.Files[0];
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            var correlationId = ResolveCorrelationId(httpContext);
            try
            {
                var result = await sender.Send(new UploadItemContentCommand(
                    sessionId,
                    itemId,
                    occurrenceKey,
                    file.FileName,
                    file.ContentType,
                    memory.ToArray(),
                    correlationId), cancellationToken);
                httpContext.Response.Headers["X-Correlation-ID"] = correlationId;
                loggerFactory.CreateLogger("OptionalUpload").LogInformation(
                    "Optional upload item completed SessionId={SessionId} ItemId={ItemId} Attempt={Attempt} Status={Status} RawBytes={RawBytes} DurationMs={DurationMs} Reason={Reason}",
                    sessionId,
                    itemId,
                    result.AttemptCount,
                    result.Status,
                    result.RawSizeBytes,
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    result.OutcomeCode);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.NotFound);
            }
            catch (UnauthorizedAccessException)
            {
                return AuthorizationErrorResults.Create(httpContext, AuthorizationErrorCodes.Forbidden);
            }
            catch (InvalidOperationException ex) when (
                ex.Message is "occurrence_key_mismatch" or "attempt_not_active" or "stale_version"
                    || ex.Message.StartsWith("Illegal upload item transition", StringComparison.Ordinal))
            {
                return ApiError(httpContext, 409, ex.Message, "The item state conflicts with this request.");
            }
        }).DisableAntiforgery();
    }

    internal static string ResolveCorrelationId(HttpContext context)
    {
        var candidate = context.Request.Headers["X-Correlation-ID"].ToString();
        var correlationId = Guid.TryParse(candidate, out var parsed)
            ? parsed.ToString()
            : Guid.NewGuid().ToString();
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        return correlationId;
    }

    internal static IResult ApiError(
        HttpContext context,
        int statusCode,
        string error,
        string message) => Results.Json(new
        {
            error,
            message,
            correlationId = ResolveCorrelationId(context),
        }, statusCode: statusCode);

    internal static IResult Validation(
        HttpContext context,
        IReadOnlyDictionary<string, string[]> errors) => Results.Json(new
        {
            error = "validation_failed",
            message = "Optional upload validation failed.",
            correlationId = ResolveCorrelationId(context),
            errors,
        }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private sealed record HeartbeatRequest(int ExpectedConcurrencyVersion);
}
