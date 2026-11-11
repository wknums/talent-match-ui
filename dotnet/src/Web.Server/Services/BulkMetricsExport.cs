using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using TalentMatch.Application.BulkScoringRequests.Services;

namespace TalentMatch.Web.Server.Services;

// The App Service Application Insights agent already sends requests, dependencies and logs, but it does not pick up
// custom meters. This exports only the bulk scoring meter (bulk.* in customMetrics); it adds no other telemetry, so
// nothing is reported twice. It does nothing when the host has no Application Insights connection string.
public static class BulkMetricsExport
{
    public static IServiceCollection AddBulkMetricsExport(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return services;

        var credential = CreateCredential(configuration["APPLICATIONINSIGHTS_AUTHENTICATION_STRING"]);
        var serviceName = configuration["OTEL_SERVICE_NAME"];
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(string.IsNullOrWhiteSpace(serviceName) ? "talentmatch-web" : serviceName))
            .WithMetrics(metrics => metrics
                .AddMeter(BulkTelemetry.MeterName)
                .AddAzureMonitorMetricExporter(options =>
                {
                    options.ConnectionString = connectionString;
                    if (credential is not null)
                        options.Credential = credential;
                }));
        return services;
    }

    // App Service hands the identity over as "Authorization=AAD;ClientId=<user-assigned identity client id>"; without a
    // client id the system-assigned identity is meant. Anything else (no value) means the instrumentation key is used.
    public static TokenCredential? CreateCredential(string? authenticationString)
    {
        if (string.IsNullOrWhiteSpace(authenticationString))
            return null;

        var parts = authenticationString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("Authorization", out var mode) || !string.Equals(mode, "AAD", StringComparison.OrdinalIgnoreCase))
            return null;

        return parts.TryGetValue("ClientId", out var clientId) && !string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
    }
}
