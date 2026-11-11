using Microsoft.Data.SqlClient;
using TalentMatch.Web.Server.Services;

namespace TalentMatch.Web.Server.Middleware;

public sealed class AzureSqlReadinessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IAzureSqlReadinessService readinessService,
        ILogger<AzureSqlReadinessMiddleware> logger)
    {
        if (!RequiresDatabase(context.Request.Path))
        {
            await next(context);
            return;
        }

        var readiness = await readinessService.WaitUntilReadyAsync(context.RequestAborted);
        if (readiness.Status != AzureSqlReadinessStatus.Ready)
        {
            await WriteUnavailableAsync(context, readiness.ErrorCode ?? "database_unavailable");
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception exception) when (ContainsSqlException(exception))
        {
            var errorCode = AzureSqlErrorClassifier.IsTransient(exception)
                ? "database_resuming"
                : "database_unavailable";
            logger.LogError(exception, "Azure SQL request failed after the readiness check ({ErrorCode}).", errorCode);
            await WriteUnavailableAsync(context, errorCode);
        }
    }

    private static bool RequiresDatabase(PathString path) =>
        path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/api/health")
        && !path.StartsWithSegments("/api/auth/config");

    private static bool ContainsSqlException(Exception exception) =>
        exception is SqlException
        || exception.InnerException is not null && ContainsSqlException(exception.InnerException);

    private static async Task WriteUnavailableAsync(HttpContext context, string errorCode)
    {
        if (context.Response.HasStarted)
            return;

        var correlationId = context.TraceIdentifier;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "10";
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await context.Response.WriteAsJsonAsync(new
        {
            error = errorCode,
            message = errorCode == "database_resuming"
                ? "The database is resuming. Retry this request shortly."
                : "The database is currently unavailable.",
            correlationId,
        });
    }
}