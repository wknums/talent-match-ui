using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.BulkScoringRequests;

public enum SharePointMode { Off, Mock, Graph }

/// <summary>
/// Deployment policy for the separate delegated Microsoft 365 source connector.
/// Source tenant, account, host, site, drive, and folder are intentionally absent: they are
/// established per connection after delegated authorization and server-side Graph verification.
/// </summary>
public sealed class BulkScoringOptions
{
    public const long MinAllowedTotalBytes = 1_048_576L;
    public const long DefaultMaxTotalBytes = 53_687_091_200L;
    public const long MaxAllowedTotalBytes = 1_099_511_627_776L;
    public const long DefaultMaxBytesPerFile = 8_388_608L;

    public static IReadOnlyList<string> RequiredSourceScopes { get; } =
        ["openid", "profile", "offline_access", "Files.Read.All", "Sites.Read.All"];

    public SharePointMode Mode { get; init; } = SharePointMode.Off;
    public string? SourceClientId { get; init; }
    public string SourceCallbackPath { get; init; } = "/api/source-connections/callback";
    public string SourceAdminConsentCallbackPath { get; init; } = "/api/source-connections/admin-consent-callback";
    public string? SourceAuthority { get; init; }
    public IReadOnlyList<string> SourceScopes { get; init; } = RequiredSourceScopes;
    public bool? SourcePublisherVerified { get; init; }

    public string? AuthKeyUri { get; init; }
    public string? AuthDevKeyPath { get; init; }
    public string? AdmissionHmacKeyUri { get; init; }
    public TimeSpan AuthorizationMaxAge { get; init; } = TimeSpan.FromHours(72);
    public TimeSpan ConnectionIdleTtl { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ConnectionAbsoluteTtl { get; init; } = TimeSpan.FromMinutes(120);

    public int PendingConnectionsPerActor { get; init; } = 10;
    public int ConnectionStartsPerActorPerMinute { get; init; } = 10;
    public int ConnectionStartsPerIpPerMinute { get; init; } = 30;
    public int CallbacksPerIpPerMinute { get; init; } = 60;

    public int SourceMaxConcurrency { get; init; } = 4;
    public int DiscoveryStepFiles { get; init; } = 1_000;
    public int DiscoveryStepMaxEntries { get; init; } = 10_000;
    public TimeSpan DiscoveryStepMaxDuration { get; init; } = TimeSpan.FromSeconds(45);
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;
    public int ImportConcurrency { get; init; } = 4;
    public int ConvertConcurrency { get; init; } = 2;
    public int MaxAttempts { get; init; } = 5;

    public int RasterDpi { get; init; } = 150;
    public int RasterJpegQuality { get; init; } = 80;
    public int RasterMaxPages { get; init; } = 30;
    public TimeSpan RasterTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public bool Enabled => Mode != SharePointMode.Off;
    public IReadOnlyList<BulkSourceKind> SupportedSources =>
        [BulkSourceKind.SharePoint, BulkSourceKind.OneDriveBusiness];

    public static BulkScoringOptions Create(
        Func<string, string?> get,
        bool isProduction,
        bool isDevelopment)
    {
        var errors = new List<string>();
        string? Text(string key) => string.IsNullOrWhiteSpace(get(key)) ? null : get(key)!.Trim();

        int Int(string key, int fallback, int min = 1, int max = int.MaxValue)
        {
            var raw = Text(key);
            if (raw is null) return fallback;
            if (!int.TryParse(raw, out var value) || value < min || value > max)
            {
                errors.Add($"{key} must be a whole number between {min} and {max}.");
                return fallback;
            }
            return value;
        }

        long Long(string key, long fallback, long min, long max)
        {
            var raw = Text(key);
            if (raw is null) return fallback;
            if (!long.TryParse(raw, out var value) || value < min || value > max)
            {
                errors.Add($"{key} must be a whole number between {min} and {max} bytes.");
                return fallback;
            }
            return value;
        }

        var modeText = Text("MICROSOFT365_SOURCE_MODE") ?? (isDevelopment ? "mock" : "off");
        var mode = modeText.ToLowerInvariant() switch
        {
            "off" => SharePointMode.Off,
            "mock" => SharePointMode.Mock,
            "graph" => SharePointMode.Graph,
            _ => SharePointMode.Off,
        };
        if (modeText.ToLowerInvariant() is not ("off" or "mock" or "graph"))
            errors.Add("MICROSOFT365_SOURCE_MODE must be off, mock, or graph.");

        var authority = Text("MICROSOFT365_SOURCE_AUTHORITY");
        var clientId = Text("MICROSOFT365_SOURCE_CLIENT_ID");
        var callback = Text("MICROSOFT365_SOURCE_CALLBACK_PATH") ?? "/api/source-connections/callback";
        var adminCallback = Text("MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH")
            ?? "/api/source-connections/admin-consent-callback";
        var keyUri = Text("BULK_AUTH_KEY_URI");
        var devKey = Text("BULK_AUTH_DEV_KEY_PATH");
        var scopeText = Text("MICROSOFT365_SOURCE_SCOPES");
        var scopes = (scopeText ?? string.Join(' ', RequiredSourceScopes))
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool? publisherVerified = bool.TryParse(Text("MICROSOFT365_SOURCE_PUBLISHER_VERIFIED"), out var parsedPublisher)
            ? parsedPublisher : null;

        if (mode == SharePointMode.Graph)
        {
            if (clientId is null) errors.Add("MICROSOFT365_SOURCE_CLIENT_ID is required in graph mode.");
            if (authority is null) errors.Add("MICROSOFT365_SOURCE_AUTHORITY is required in graph mode.");
            else if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
                     || !authorityUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                     || !authorityUri.AbsolutePath.TrimEnd('/').EndsWith("/organizations", StringComparison.OrdinalIgnoreCase))
                errors.Add("MICROSOFT365_SOURCE_AUTHORITY must be an HTTPS organizational authority ending in /organizations.");
            if (keyUri is null && devKey is null)
                errors.Add("BULK_AUTH_KEY_URI is required in graph mode (or BULK_AUTH_DEV_KEY_PATH outside Production).");
        }

        if (!IsLocalAbsolutePath(callback))
            errors.Add("MICROSOFT365_SOURCE_CALLBACK_PATH must be a local absolute path.");
        if (!IsLocalAbsolutePath(adminCallback))
            errors.Add("MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH must be a local absolute path.");
        if (!RequiredSourceScopes.SequenceEqual(scopes, StringComparer.Ordinal))
            errors.Add("MICROSOFT365_SOURCE_SCOPES must contain only the required delegated scopes in canonical order.");
        if (isProduction && devKey is not null)
            errors.Add("BULK_AUTH_DEV_KEY_PATH is rejected in Production.");

        var options = new BulkScoringOptions
        {
            Mode = mode,
            SourceClientId = clientId,
            SourceCallbackPath = callback,
            SourceAdminConsentCallbackPath = adminCallback,
            SourceAuthority = authority,
            SourceScopes = scopes,
            SourcePublisherVerified = publisherVerified,
            AuthKeyUri = keyUri,
            AuthDevKeyPath = devKey,
            AdmissionHmacKeyUri = Text("BULK_ADMISSION_HMAC_KEY_URI"),
            AuthorizationMaxAge = TimeSpan.FromHours(Int("BULK_AUTH_MAX_AGE_HOURS", 72)),
            ConnectionIdleTtl = TimeSpan.FromMinutes(Int("BULK_SOURCE_IDLE_TTL_MINUTES", 30, 1, 120)),
            ConnectionAbsoluteTtl = TimeSpan.FromMinutes(Int("BULK_SOURCE_ABSOLUTE_TTL_MINUTES", 120, 1, 1440)),
            PendingConnectionsPerActor = Int("BULK_PENDING_CONNECTIONS_PER_ACTOR", 10, 1, 100),
            ConnectionStartsPerActorPerMinute = Int("BULK_CONNECTION_STARTS_PER_ACTOR_PER_MINUTE", 10, 1, 1000),
            ConnectionStartsPerIpPerMinute = Int("BULK_CONNECTION_STARTS_PER_IP_PER_MINUTE", 30, 1, 1000),
            CallbacksPerIpPerMinute = Int("BULK_CALLBACKS_PER_IP_PER_MINUTE", 60, 1, 5000),
            SourceMaxConcurrency = Int("MICROSOFT365_SOURCE_MAX_CONCURRENCY", 4, 1, 64),
            DiscoveryStepFiles = Int("BULK_DISCOVERY_STEP_FILES", 1_000, 1, 1_000),
            DiscoveryStepMaxEntries = Int("BULK_DISCOVERY_STEP_MAX_ENTRIES", 10_000),
            DiscoveryStepMaxDuration = TimeSpan.FromSeconds(Int("BULK_DISCOVERY_STEP_MAX_SECONDS", 45)),
            MaxTotalBytes = Long("BULK_MAX_TOTAL_BYTES", DefaultMaxTotalBytes, MinAllowedTotalBytes, MaxAllowedTotalBytes),
            ImportConcurrency = Int("BULK_IMPORT_CONCURRENCY", 4, 1, 64),
            ConvertConcurrency = Int("BULK_CONVERT_CONCURRENCY", 2, 1, 64),
            MaxAttempts = Int("BULK_MAX_ATTEMPTS", 5, 1, 20),
            RasterDpi = Int("BULK_RASTER_DPI", 150, 72, 600),
            RasterJpegQuality = Int("BULK_RASTER_JPEG_QUALITY", 80, 1, 100),
            RasterMaxPages = Int("BULK_RASTER_MAX_PAGES", 30, 1, 1000),
        };

        if (options.ConnectionIdleTtl >= options.ConnectionAbsoluteTtl)
            errors.Add("BULK_SOURCE_IDLE_TTL_MINUTES must be less than BULK_SOURCE_ABSOLUTE_TTL_MINUTES.");
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid bulk scoring configuration: " + string.Join(" ", errors));
        return options;
    }

    private static bool IsLocalAbsolutePath(string value) =>
        value.Length > 1
        && value[0] == '/'
        && value[1] != '/'
        && !value.Contains('\\')
        && !value.Contains('?')
        && !value.Contains('#')
        && !value.Any(char.IsControl);
}
