using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure.HostedServices;
using TalentMatch.Infrastructure.SharePoint;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkScoringRegistrationTests
{
    [Theory]
    [InlineData("off", false)]
    [InlineData("mock", true)]
    public void Worker_and_rasterizer_are_registered_only_when_the_delegated_feature_is_on(
        string mode,
        bool expected)
    {
        var services = Services(new Dictionary<string, string?>
        {
            ["MICROSOFT365_SOURCE_MODE"] = mode,
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        });

        services.Any(value =>
                value.ServiceType == typeof(IHostedService)
                && value.ImplementationType == typeof(BulkScoringWorker))
            .Should().Be(expected);
        services.Any(value => value.ServiceType == typeof(IDocumentRasterizer))
            .Should().Be(expected);
        services.Any(value => value.ServiceType == typeof(IBulkContentStore))
            .Should().BeTrue();
    }

    [Fact]
    public void Graph_mode_registers_only_the_request_bound_delegated_authorization_store()
    {
        var services = Services(new Dictionary<string, string?>
        {
            ["MICROSOFT365_SOURCE_MODE"] = "graph",
            ["MICROSOFT365_SOURCE_CLIENT_ID"] = "connector-client",
            ["MICROSOFT365_SOURCE_AUTHORITY"] = "https://login.example/organizations",
            ["BULK_AUTH_KEY_URI"] = "https://vault.example/keys/bulk-auth",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        });

        services.Single(value => value.ServiceType == typeof(IRequestAuthorizationStore))
            .ImplementationType.Should().Be(typeof(RequestAuthorizationStore));
        services.Any(value => value.ServiceType == typeof(IAuthorizationKeyWrapper))
            .Should().BeTrue();
    }

    [Fact]
    public void Default_outside_development_is_off()
    {
        var services = Services(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        });

        services.Any(value => value.ImplementationType == typeof(BulkScoringWorker))
            .Should().BeFalse();
    }

    [Fact]
    public void Azure_runtime_production_uses_the_explicit_QA_product_environment_for_publisher_activation()
    {
        var services = Services(new Dictionary<string, string?>
        {
            ["MICROSOFT365_SOURCE_MODE"] = "graph",
            ["MICROSOFT365_SOURCE_CLIENT_ID"] = "connector-client",
            ["MICROSOFT365_SOURCE_AUTHORITY"] = "https://login.microsoftonline.com/organizations",
            ["BULK_AUTH_KEY_URI"] = "https://vault.example/keys/bulk-auth",
            ["MICROSOFT365_SOURCE_PUBLISHER_VERIFIED"] = "false",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["PRODUCT_ENVIRONMENT"] = "QA",
        });

        services.Any(value => value.ImplementationType == typeof(BulkScoringWorker))
            .Should().BeTrue();
    }

    [Fact]
    public void Product_production_still_requires_verified_publisher()
    {
        var action = () => Services(new Dictionary<string, string?>
        {
            ["MICROSOFT365_SOURCE_MODE"] = "graph",
            ["MICROSOFT365_SOURCE_CLIENT_ID"] = "connector-client",
            ["MICROSOFT365_SOURCE_AUTHORITY"] = "https://login.microsoftonline.com/organizations",
            ["BULK_AUTH_KEY_URI"] = "https://vault.example/keys/bulk-auth",
            ["MICROSOFT365_SOURCE_PUBLISHER_VERIFIED"] = "false",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["PRODUCT_ENVIRONMENT"] = "Production",
        });

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*source connector is unavailable*");
    }

    private static ServiceCollection Services(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddBulkScoring(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services;
    }
}
