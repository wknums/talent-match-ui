using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using TalentMatch.Web.Server.Services;

namespace TalentMatch.Web.Tests;

public sealed class BulkMetricsExportTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    [Fact]
    public void Nothing_is_registered_when_the_host_has_no_application_insights_connection_string()
    {
        var services = new ServiceCollection();

        services.AddBulkMetricsExport(Config());

        services.Should().BeEmpty();
    }

    [Fact]
    public void A_blank_connection_string_counts_as_none()
    {
        var services = new ServiceCollection();

        services.AddBulkMetricsExport(Config(("APPLICATIONINSIGHTS_CONNECTION_STRING", "  ")));

        services.Should().BeEmpty();
    }

    [Fact]
    public void The_bulk_meter_is_exported_when_a_connection_string_is_present()
    {
        var services = new ServiceCollection();

        services.AddBulkMetricsExport(Config(
            ("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000"),
            ("OTEL_SERVICE_NAME", "app-test")));

        services.Should().Contain(d => d.ServiceType == typeof(MeterProvider));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ClientId=abc")]
    [InlineData("Authorization=Bearer;ClientId=abc")]
    [InlineData("garbage")]
    public void Without_an_entra_authorization_the_instrumentation_key_is_used(string? authenticationString)
        => BulkMetricsExport.CreateCredential(authenticationString).Should().BeNull();

    [Theory]
    [InlineData("Authorization=AAD;ClientId=747c516e-a791-4a6d-a518-999ede48dc9c")]
    [InlineData("authorization=aad; clientid=747c516e-a791-4a6d-a518-999ede48dc9c")]
    [InlineData("Authorization=AAD")]
    [InlineData("Authorization=AAD;ClientId=")]
    public void An_entra_authorization_uses_the_managed_identity(string authenticationString)
        => BulkMetricsExport.CreateCredential(authenticationString).Should().BeOfType<ManagedIdentityCredential>();
}
