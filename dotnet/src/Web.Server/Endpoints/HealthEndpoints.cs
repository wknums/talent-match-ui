using Microsoft.EntityFrameworkCore;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Server.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(string.Empty).WithTags("Health");

        group.MapGet("/api/health", CheckHealthAsync).AllowAnonymous();
        group.MapGet("/healthz", CheckHealthAsync).AllowAnonymous();
    }

    private static async Task<IResult> CheckHealthAsync(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var awrApi = await CheckAwrApiHealthAsync(httpClientFactory, cancellationToken);
        var azureSql = await CheckAzureSqlHealthAsync(db, cancellationToken);
        var status = awrApi.Status == "failed" || azureSql.Status == "failed" ? "unhealthy" : "ok";

        var payload = new
        {
            status,
            stack = "stack-b",
            storage = db.Database.IsSqlServer() ? "azure-sql" : "sqlite",
            timestamp = DateTimeOffset.UtcNow,
            checks = new
            {
                awrApi,
                azureSql,
            }
        };

        return status == "ok"
            ? Results.Ok(payload)
            : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<HealthDependencyCheck> CheckAwrApiHealthAsync(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new HealthDependencyCheck("awr-api", "skipped")
            {
                Detail = "AWR_SEQ_API_ENDPOINT is not configured."
            };
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            return new HealthDependencyCheck("awr-api", "failed")
            {
                Detail = "AWR_SEQ_API_ENDPOINT is not a valid URL."
            };
        }

        var target = new Uri(endpointUri, "/healthz");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var client = httpClientFactory.CreateClient();
            var response = await client.GetAsync(target, timeoutCts.Token);
            var latencyMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                return new HealthDependencyCheck("awr-api", "failed")
                {
                    Target = target.ToString(),
                    LatencyMs = latencyMs,
                    Detail = $"Health endpoint returned HTTP {(int)response.StatusCode}."
                };
            }

            return new HealthDependencyCheck("awr-api", "ok")
            {
                Target = target.ToString(),
                LatencyMs = latencyMs
            };
        }
        catch (Exception ex)
        {
            return new HealthDependencyCheck("awr-api", "failed")
            {
                Target = target.ToString(),
                LatencyMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
                Detail = ex.Message
            };
        }
    }

    private static async Task<HealthDependencyCheck> CheckAzureSqlHealthAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
        {
            return new HealthDependencyCheck("azure-sql", "skipped")
            {
                Detail = "Azure SQL is not enabled for this environment."
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeoutCts.Token);

            return new HealthDependencyCheck("azure-sql", "ok")
            {
                Target = Environment.GetEnvironmentVariable("AZURE_SQL_SERVER_FQDN"),
                LatencyMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new HealthDependencyCheck("azure-sql", "failed")
            {
                Target = Environment.GetEnvironmentVariable("AZURE_SQL_SERVER_FQDN"),
                LatencyMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
                Detail = ex.Message
            };
        }
    }
}

public sealed class HealthDependencyCheck
{
    public HealthDependencyCheck(string name, string status)
    {
        Name = name;
        Status = status;
    }

    public string Name { get; }

    public string Status { get; }

    public string? Target { get; init; }

    public long? LatencyMs { get; init; }

    public string? Detail { get; init; }
}
