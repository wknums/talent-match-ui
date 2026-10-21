using MediatR;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Queries;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public static class BulkScoringRequestsEndpoints
{
    public static void MapBulkScoringRequestsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Bulk scoring requests").RequireAuthorization();

        group.MapGet("/bulk-scoring-requests/capabilities", (ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, () => sender.Send(new GetBulkScoringCapabilitiesQuery(), ct)));
        group.MapGet("/bulk-scoring-capabilities", (ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, () => sender.Send(new GetBulkScoringCapabilitiesQuery(), ct)));

        var settings = app.MapGroup("/api/bulk-scoring-settings").WithTags("Bulk scoring settings").RequireAuthorization("AdminOnly");
        settings.MapGet("/", (ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, () => sender.Send(new GetBulkScoringSettingsQuery(), ct)));
        settings.MapPut("/", (UpdateBulkSettingsRequest body, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, () => sender.Send(new UpdateBulkScoringSettingsCommand(body, UploadEndpoints.ResolveCorrelationId(http)), ct)));

        group.MapPost("/jobs/{jobId}/bulk-scoring-requests/preview", (string jobId, BulkPreviewRequest body, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Json(await sender.Send(
                new PreviewBulkScoringRequestCommand(jobId, body.FolderUrl, UploadEndpoints.ResolveCorrelationId(http)),
                ct), statusCode: 201)));

        group.MapPost("/bulk-scoring-requests/{requestId}/discover-more", (string requestId, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, () => sender.Send(new DiscoverMoreBulkScoringRequestCommand(
                requestId,
                UploadEndpoints.ResolveCorrelationId(http)), ct)));

        group.MapGet("/bulk-scoring-requests/{requestId}", (string requestId, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, () => sender.Send(new GetBulkScoringRequestQuery(requestId), ct)))
            .Produces<BulkRequestDetailDto>();

        group.MapDelete("/bulk-scoring-requests/{requestId}", (string requestId, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, async () =>
            {
                await sender.Send(new AbandonBulkScoringRequestDraftCommand(
                    requestId,
                    UploadEndpoints.ResolveCorrelationId(http)), ct);
                return Results.NoContent();
            }));

        group.MapPost("/bulk-scoring-requests/{requestId}/confirm", (string requestId, BulkConfirmRequest body, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, async () =>
            {
                var result = await sender.Send(new ConfirmBulkScoringRequestCommand(requestId, body, UploadEndpoints.ResolveCorrelationId(http)), ct);
                return Results.Json(result.Request, statusCode: result.Created ? 201 : 200);
            }));

        group.MapGet("/bulk-scoring-requests/{requestId}/items", (
            string requestId, ISender sender, HttpContext http, CancellationToken ct,
            int page = 1, int pageSize = 50, string? status = null, string? q = null) =>
            Run(http, () => sender.Send(new GetBulkScoringItemsQuery(requestId, page, pageSize, status, q), ct)))
            .Produces<BulkItemPageDto>();

        group.MapGet("/bulk-scoring-requests/{requestId}/draft-items", (
            string requestId,
            string? reviewCursor,
            int pageSize,
            ISender sender,
            HttpContext http,
            CancellationToken ct) =>
            Run(http, () => sender.Send(
                new DraftItemReviewQuery(requestId, reviewCursor, pageSize <= 0 ? 100 : pageSize),
                ct)));

        group.MapGet("/jobs/{jobId}/bulk-scoring-requests", (
            string jobId, ISender sender, HttpContext http, CancellationToken ct, int page = 1, int pageSize = 50) =>
            Run(http, () => sender.Send(new ListBulkScoringRequestsQuery(jobId, page, pageSize), ct)))
            .Produces<BulkRequestPageDto>();

        group.MapGet("/bulk-scoring-requests/notifications", (ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, () => sender.Send(new ListBulkScoringNotificationsQuery(), ct)))
            .Produces<IReadOnlyList<BulkTerminalNotificationDto>>();

        group.MapPost("/bulk-scoring-requests/{requestId}/acknowledge", (string requestId, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, async () =>
            {
                await sender.Send(new AcknowledgeBulkScoringRequestCommand(
                    requestId,
                    UploadEndpoints.ResolveCorrelationId(http)), ct);
                return Results.NoContent();
            }))
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/bulk-scoring-requests/{requestId}/items/{itemId}/original", (string requestId, string itemId, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, async () =>
            {
                var file = await sender.Send(new GetBulkScoringOriginalQuery(requestId, itemId), ct);
                // Untrusted content is never rendered inline.
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.File(file.Content, "application/octet-stream", file.FileName);
            }));

        group.MapPost("/bulk-scoring-requests/{requestId}/cancel", (string requestId, CancelBulkScoringBody body, ISender sender, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Json(
                await sender.Send(new CancelBulkScoringRequestCommand(requestId, body.ExpectedConcurrencyVersion, UploadEndpoints.ResolveCorrelationId(http)), ct),
                statusCode: StatusCodes.Status202Accepted)))
            .Produces<BulkRequestDetailDto>(StatusCodes.Status202Accepted);

        group.MapPost("/bulk-scoring-requests/{requestId}/retry-failed", (
            string requestId,
            RetryBulkRequestBody? body,
            ISender sender,
            HttpContext http,
            CancellationToken ct) =>
            Run(http, async () => Results.Json(
                await sender.Send(new RetryFailedBulkScoringRequestItemsCommand(
                    requestId,
                    UploadEndpoints.ResolveCorrelationId(http),
                    body?.ConnectionId), ct),
                statusCode: StatusCodes.Status201Created)))
            .Produces<BulkRequestDetailDto>(StatusCodes.Status201Created);
    }

    public sealed record CancelBulkScoringBody(int ExpectedConcurrencyVersion);
    public sealed record RetryBulkRequestBody(string? ConnectionId);

    // One place that turns application failures into the contract's Error shape.
    private static async Task<IResult> Run<T>(HttpContext http, Func<Task<T>> action)
    {
        BulkEndpointResults.SetCorrelationHeader(http);
        try
        {
            var result = await action();
            return result is IResult ready ? ready : Results.Ok(result);
        }
        catch (BulkApiException ex) { return BulkEndpointResults.Fail(http, ex.StatusCode, ex.Code, ex.ErrorsOrNull()); }
        catch (BulkValidationException ex) { return BulkEndpointResults.Fail(http, 422, "validation_failed", ex.Errors); }
        catch (UnauthorizedAccessException) { return BulkEndpointResults.Fail(http, 403, "forbidden"); }
    }
}

internal static class BulkEndpointResults
{
    public static void SetCorrelationHeader(HttpContext http) =>
        http.Response.Headers["X-Correlation-ID"] = UploadEndpoints.ResolveCorrelationId(http);

    public static IResult Fail(
        HttpContext http,
        int status,
        string code,
        IReadOnlyDictionary<string, string[]>? errors = null,
        string? adminConsentUrl = null,
        bool includeMessage = false)
    {
        SetCorrelationHeader(http);
        var message = BulkSourceDisclosurePolicy.SafeProblemDetail(status, code);
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = UploadEndpoints.ResolveCorrelationId(http),
        };
        if (includeMessage) extensions["message"] = message;
        if (errors is not null) extensions["errors"] = errors;
        if (adminConsentUrl is not null) extensions["adminConsentUrl"] = adminConsentUrl;
        return Results.Problem(
            type: $"urn:talentmatch:problem:{code}",
            title: StatusTitle(status),
            statusCode: status,
            detail: message,
            extensions: extensions);
    }

    private static string StatusTitle(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        410 => "Gone",
        422 => "Unprocessable Content",
        429 => "Too Many Requests",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Request Failed",
    };
}

internal static class BulkApiExceptionExtensions
{
    public static IReadOnlyDictionary<string, string[]>? ErrorsOrNull(this BulkApiException _) => null;
}
