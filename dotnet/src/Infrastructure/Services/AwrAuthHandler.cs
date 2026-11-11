using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace TalentMatch.Infrastructure.Services;

/// <summary>
/// DelegatingHandler that adds authentication headers to outbound requests
/// to the AWReason API based on the AWR_AUTH_MODE environment variable.
/// Modes: none (local dev), apikey (staging), entra (production).
/// </summary>
public class AwrAuthHandler : DelegatingHandler
{
    private readonly ILogger<AwrAuthHandler> _logger;
    private DefaultAzureCredential? _credential;

    public AwrAuthHandler(ILogger<AwrAuthHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var mode = (Environment.GetEnvironmentVariable("AWR_AUTH_MODE") ?? "none").ToLowerInvariant();

        switch (mode)
        {
            case "none":
                break;

            case "apikey":
            {
                var apiKey = Environment.GetEnvironmentVariable("AWR_API_KEY")
                    ?? throw new InvalidOperationException(
                        "AWR_AUTH_MODE is \"apikey\" but AWR_API_KEY is not set.");

                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

                // Propagate user identity if available via custom request options
                if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>("AwrUserId"), out var userId))
                    request.Headers.TryAddWithoutValidation("X-User-Id", userId);

                if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>("AwrUserRole"), out var userRole))
                    request.Headers.TryAddWithoutValidation("X-User-Role", userRole);

                break;
            }

            case "entra":
            {
                var audience = Environment.GetEnvironmentVariable("AWR_AAD_AUDIENCE")
                    ?? throw new InvalidOperationException(
                        "AWR_AUTH_MODE is \"entra\" but AWR_AAD_AUDIENCE is not set.");

                _credential ??= new DefaultAzureCredential();
                var token = await _credential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext(new[] { audience }),
                    cancellationToken);

                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                break;
            }

            default:
                throw new InvalidOperationException(
                    $"Invalid AWR_AUTH_MODE: \"{mode}\". Valid values: none, apikey, entra.");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Validates that required AWR auth environment variables are set.
    /// Call at startup to fail fast on misconfiguration.
    /// </summary>
    public static void ValidateConfiguration()
    {
        var mode = (Environment.GetEnvironmentVariable("AWR_AUTH_MODE") ?? "none").ToLowerInvariant();

        switch (mode)
        {
            case "none":
                break;
            case "apikey":
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWR_API_KEY")))
                    throw new InvalidOperationException(
                        "AWR_AUTH_MODE is \"apikey\" but AWR_API_KEY is not set.");
                break;
            case "entra":
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWR_AAD_AUDIENCE")))
                    throw new InvalidOperationException(
                        "AWR_AUTH_MODE is \"entra\" but AWR_AAD_AUDIENCE is not set.");
                break;
            default:
                throw new InvalidOperationException(
                    $"Invalid AWR_AUTH_MODE: \"{mode}\". Valid values: none, apikey, entra.");
        }
    }
}
