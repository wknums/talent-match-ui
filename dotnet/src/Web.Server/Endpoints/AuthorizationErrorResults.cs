using TalentMatch.Application.Authorization;

namespace TalentMatch.Web.Server.Endpoints;

internal static class AuthorizationErrorResults
{
    private const string CorrelationItemKey = "TalentMatch.CorrelationId";

    public static string EnsureCorrelationId(HttpContext httpContext)
    {
        if (httpContext.Items[CorrelationItemKey] is not string correlationId)
        {
            correlationId = Guid.NewGuid().ToString();
            httpContext.Items[CorrelationItemKey] = correlationId;
        }

        httpContext.Response.Headers["X-Correlation-ID"] = correlationId;
        return correlationId;
    }

    /// <summary>Adopts a caller-supplied correlation id so client and server audit trails match.</summary>
    public static string UseCorrelationId(HttpContext httpContext, string correlationId)
    {
        httpContext.Items[CorrelationItemKey] = correlationId;
        httpContext.Response.Headers["X-Correlation-ID"] = correlationId;
        return correlationId;
    }

    public static IResult Create(HttpContext httpContext, string code)
    {
        var error = AuthorizationErrorCodes.Resolve(code);
        return Create(httpContext, error);
    }

    public static IResult Create(HttpContext httpContext, AuthorizationErrorDefinition error)
    {
        var correlationId = EnsureCorrelationId(httpContext);
        return Results.Json(new
        {
            error = error.Code,
            message = error.Message,
            correlationId,
        }, statusCode: error.StatusCode);
    }

    public static async Task WriteAsync(
        HttpContext httpContext,
        string code,
        CancellationToken cancellationToken = default)
    {
        var error = AuthorizationErrorCodes.Resolve(code);
        var correlationId = EnsureCorrelationId(httpContext);
        httpContext.Response.StatusCode = error.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(new
        {
            error = error.Code,
            message = error.Message,
            correlationId,
        }, cancellationToken);
    }
}