using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.BulkScoringRequests.SourceConnections;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Web.Server.Endpoints;

public static class SourceConnectionsEndpoints
{
    private const string CorrelationPurpose = "TalentMatch.SourceConnectionCorrelation.v1";

    public static void MapSourceConnectionsEndpoints(this WebApplication app)
    {
        var authorized = app.MapGroup("/api").WithTags("Source connections").RequireAuthorization();

        authorized.MapPost("/jobs/{jobId}/source-connections", (
            string jobId,
            BeginSourceConnectionRequest body,
            ISender sender,
            IDataProtectionProvider protection,
            HttpContext http,
            CancellationToken ct) =>
            Run(http, async () =>
            {
                var result = await sender.Send(new BeginSourceConnectionCommand(
                    jobId,
                    body,
                    AbsoluteCallback(http, "/api/source-connections/callback"),
                    UploadEndpoints.ResolveCorrelationId(http)), ct);
                if (result.Status == BulkSourceConnectionStatus.Authorizing.ToWire())
                {
                    var state = QueryHelpers.ParseQuery(result.AuthorizationUrl.Query)["state"].ToString();
                    if (string.IsNullOrWhiteSpace(state))
                        throw new InvalidOperationException("The source authorization URL has no callback state.");
                    var expiresAt = new DateTimeOffset(DateTime.SpecifyKind(result.ExpiresAt, DateTimeKind.Utc));
                    var cookie = CorrelationCookieOptions();
                    cookie.Expires = expiresAt;
                    http.Response.Cookies.Append(
                        CorrelationCookieName(state),
                        protection.CreateProtector(CorrelationPurpose)
                            .ToTimeLimitedDataProtector().Protect(state, expiresAt),
                        cookie);
                }
                return Results.Json(result, statusCode: StatusCodes.Status201Created);
            }));

        authorized.MapGet("/source-connections/{connectionId}", GetAsync);
        authorized.MapPost("/source-connections/{connectionId}/preview", (
            string connectionId,
            ISender sender,
            HttpContext http,
            CancellationToken ct) =>
            Run(http, async () => Results.Json(
                await sender.Send(new PreviewSourceConnectionCommand(
                    connectionId,
                    UploadEndpoints.ResolveCorrelationId(http)), ct),
                statusCode: StatusCodes.Status201Created)));
        authorized.MapDelete("/source-connections/{connectionId}", AbandonAsync);

        app.MapGet("/api/source-connections/callback", (
            string? state,
            string? code,
            string? error,
            string? error_description,
            ISender sender,
            IDataProtectionProvider protection,
            HttpContext http,
            CancellationToken ct) =>
            Run(http, async () =>
            {
                if (string.IsNullOrWhiteSpace(state))
                    throw new BulkApiException(
                        400,
                        "source_connection_invalid_or_expired",
                        "The source connection callback is invalid.");
                ValidateBrowserCorrelation(http, state, protection);
                var result = await sender.Send(
                    new CompleteSourceConnectionCallbackCommand(
                        new SourceConnectionCallbackRequest(state, code, error, error_description),
                        AbsoluteCallback(http, "/api/source-connections/callback")),
                    ct);
                return Results.Redirect(result.RedirectUri.ToString());
            }))
            .AllowAnonymous();

        app.MapGet("/api/source-connections/admin-consent-callback", (
            string? state,
            string? error,
            HttpContext http) =>
            Run(http, () =>
            {
                if (string.IsNullOrWhiteSpace(state) || !string.IsNullOrWhiteSpace(error))
                    throw new BulkApiException(
                        400,
                        "source_connection_invalid_or_expired",
                        "The source connection callback is invalid.");
                return Task.FromResult<IResult>(Results.Redirect("/"));
            }))
            .AllowAnonymous();
    }

    private static Uri AbsoluteCallback(HttpContext http, string path) =>
        new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{path}");

    private static string CorrelationCookieName(string state) =>
        "__Host-TalentMatch.SourceConnection."
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static CookieOptions CorrelationCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
    };

    private static void ValidateBrowserCorrelation(
        HttpContext http, string state, IDataProtectionProvider protection)
    {
        // The anonymous callback must prove it came from the browser that started this actor-bound state.
        var name = CorrelationCookieName(state);
        if (!http.Request.Cookies.TryGetValue(name, out var cookie))
            throw InvalidCorrelation();
        http.Response.Cookies.Delete(name, CorrelationCookieOptions());
        string protectedState;
        try
        {
            protectedState = protection.CreateProtector(CorrelationPurpose)
                .ToTimeLimitedDataProtector().Unprotect(cookie);
        }
        catch (CryptographicException)
        {
            throw InvalidCorrelation();
        }
        if (!string.Equals(protectedState, state, StringComparison.Ordinal))
            throw InvalidCorrelation();
    }

    private static BulkApiException InvalidCorrelation() =>
        new(400, "source_connection_invalid_or_expired", "The source connection callback is invalid.");

    private static async Task<IResult> GetAsync(
        string connectionId,
        BulkScoringAccess access,
        IBulkSourceConnectionRepository repository,
        HttpContext http,
        CancellationToken ct) =>
        await Run(http, async () =>
        {
            access.EnsureEnabled();
            var connection = await repository.GetOwnedAsync(connectionId, access.Actor, ct)
                ?? throw BulkApiException.NotFound("The source connection was not found.");
            return Results.Ok(new SourceConnectionStatusResponse(
                connection.Id,
                connection.Status.ToWire(),
                DateTime.SpecifyKind(connection.ExpiresAt, DateTimeKind.Utc),
                connection.SourceKind?.ToWire(),
                connection.SourceAccountDisplay,
                connection.SourceFolderName,
                false,
                null,
                connection.FailureCode));
        });

    private static async Task<IResult> AbandonAsync(
        string connectionId,
        BulkScoringAccess access,
        IBulkSourceConnectionRepository repository,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct) =>
        await Run(http, async () =>
        {
            access.EnsureEnabled();
            var connection = await repository.GetOwnedAsync(connectionId, access.Actor, ct)
                ?? throw BulkApiException.NotFound("The source connection was not found.");
            if (connection.IsPending)
            {
                var now = clock.GetUtcNow().UtcDateTime;
                await repository.AbandonWithAuditAsync(
                    connection.Id,
                    access.Actor,
                    connection.JobId,
                    now,
                    BulkAuditWriter.User(
                        BulkAuditEventTypes.PreviewDraftAbandoned,
                        access.Actor,
                        UploadEndpoints.ResolveCorrelationId(http),
                        now,
                        "source_connection",
                        connection.Id,
                        new { state = BulkSourceConnectionStatus.Abandoned.ToWire() }),
                    ct);
            }
            return Results.NoContent();
        });

    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        BulkEndpointResults.SetCorrelationHeader(http);
        try
        {
            return await action();
        }
        catch (BulkApiException ex)
        {
            var code = ex.Code == "unsupported_source"
                ? "source_folder_invalid_or_unavailable"
                : ex.Code;
            return BulkEndpointResults.Fail(http, ex.StatusCode, code, includeMessage: false);
        }
        catch (UnauthorizedAccessException)
        {
            return BulkEndpointResults.Fail(http, 403, "forbidden", includeMessage: false);
        }
    }

    private sealed record SourceConnectionStatusResponse(
        string ConnectionId,
        string Status,
        DateTime ExpiresAt,
        string? SourceKind,
        string? SourceAccountDisplay,
        string? FolderName,
        bool AdminConsentRequired,
        string? AdminConsentUrl,
        string? SafeFailureCode);
}
