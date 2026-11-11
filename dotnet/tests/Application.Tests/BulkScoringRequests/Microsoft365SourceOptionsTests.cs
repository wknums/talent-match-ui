using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class Microsoft365SourceOptionsTests
{
    [Fact]
    public void Options_surface_is_delegated_commercial_cloud_and_contains_no_legacy_reader_inputs()
    {
        var properties = typeof(BulkScoringOptions).GetProperties().Select(property => property.Name).ToArray();
        properties.Should().Contain(
            [
                "SourceClientId", "SourceCallbackPath", "SourceAdminConsentCallbackPath",
                "SourceAuthority", "SourceScopes", "SourcePublisherVerified",
                "ConnectionIdleTtl", "ConnectionAbsoluteTtl",
                "PendingConnectionsPerActor", "ConnectionStartsPerActorPerMinute",
                "ConnectionStartsPerIpPerMinute", "CallbacksPerIpPerMinute",
            ]);
        properties.Should().NotContain(
            [
                "AccessModel", "HomeTenantId", "SourceTenantId", "ClientSecret",
                "ClientCertPath", "ClientCertPassword", "SharePointHosts",
                "OneDriveHosts", "PermittedRoots", "ApiClientId",
            ]);
    }

    [Fact]
    public void Limits_freeze_exact_file_inflight_count_and_aggregate_bounds()
    {
        var type = typeof(BulkScoringOptions);
        type.GetField("MaxAllowedTotalBytes")!.GetRawConstantValue().Should().Be(1_099_511_627_776L);
        type.GetField("MinAllowedTotalBytes")!.GetRawConstantValue().Should().Be(1_048_576L);
        type.GetField("DefaultMaxTotalBytes")!.GetRawConstantValue().Should().Be(53_687_091_200L);
        ((IReadOnlyList<string>)type.GetProperty("RequiredSourceScopes")!
            .GetValue(null)!).Should().Equal(
            "openid", "profile", "offline_access", "Files.Read.All", "Sites.Read.All");
    }

    [Fact]
    public void Deployed_callback_paths_are_accepted_as_local_absolute_paths()
    {
        var values = GraphSettings();

        var options = BulkScoringOptions.Create(
            key => values.GetValueOrDefault(key),
            isProduction: false,
            isDevelopment: false);

        options.SourceCallbackPath.Should().Be("/api/source-connections/callback");
        options.SourceAdminConsentCallbackPath.Should().Be("/api/source-connections/admin-consent-callback");
    }

    [Theory]
    [InlineData("https://app.example/api/source-connections/callback")]
    [InlineData("//app.example/api/source-connections/callback")]
    [InlineData(@"\api\source-connections\callback")]
    [InlineData("/api/source-connections/callback?code=unsafe")]
    [InlineData("/api/source-connections/callback#fragment")]
    public void Non_local_callback_paths_are_rejected(string callback)
    {
        var values = GraphSettings();
        values["MICROSOFT365_SOURCE_CALLBACK_PATH"] = callback;

        var action = () => BulkScoringOptions.Create(
            key => values.GetValueOrDefault(key),
            isProduction: false,
            isDevelopment: false);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*MICROSOFT365_SOURCE_CALLBACK_PATH must be a local absolute path.*");
    }

    private static Dictionary<string, string?> GraphSettings() => new()
    {
        ["MICROSOFT365_SOURCE_MODE"] = "graph",
        ["MICROSOFT365_SOURCE_CLIENT_ID"] = "connector-client-id",
        ["MICROSOFT365_SOURCE_AUTHORITY"] = "https://login.microsoftonline.com/organizations",
        ["MICROSOFT365_SOURCE_CALLBACK_PATH"] = "/api/source-connections/callback",
        ["MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH"] = "/api/source-connections/admin-consent-callback",
        ["BULK_AUTH_DEV_KEY_PATH"] = "artifacts/test-bulk-auth.key",
    };
}
