using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.BulkScoringRequests.SourceConnections;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure.HostedServices;
using TalentMatch.Infrastructure.Rasterization;
using TalentMatch.Infrastructure.SharePoint.Authorization;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.SharePoint;

public static class BulkScoringServiceCollectionExtensions
{
    // MICROSOFT365_SOURCE_MODE=off registers options only: no source authorization or worker.
    public static IServiceCollection AddBulkScoring(this IServiceCollection services, IConfiguration configuration)
    {
        var runtimeEnvironment = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"]
            ?? "Production";
        var configuredProductEnvironment = configuration["PRODUCT_ENVIRONMENT"];
        var productEnvironment = string.IsNullOrWhiteSpace(configuredProductEnvironment)
            ? runtimeEnvironment
            : configuredProductEnvironment.Trim();
        var isProduction = string.Equals(runtimeEnvironment, "Production", StringComparison.OrdinalIgnoreCase);
        var options = BulkScoringOptions.Create(
            key => configuration[key],
            isProduction: isProduction,
            isDevelopment: string.Equals(runtimeEnvironment, "Development", StringComparison.OrdinalIgnoreCase));

        var publisherVerified = bool.TryParse(
            configuration["MICROSOFT365_SOURCE_PUBLISHER_VERIFIED"],
            out var verified)
            ? verified
            : (bool?)null;
        if (options.Enabled)
            Microsoft365PublisherGate.RequireActivation(productEnvironment, publisherVerified);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        if (options.Mode == SharePointMode.Mock)
        {
            services.AddSingleton<IAuthorizationKeyWrapper>(_ => new LocalAuthorizationKeyWrapper(options.AuthDevKeyPath));
            services.AddSingleton<MockAuthorizationControl>();
            services.AddScoped<IRequestAuthorizationStore, MockRequestAuthorizationStore>();
            services.AddSingleton<ISharePointAccessContext, MockSharePointAccessContext>();
            services.AddSingleton<MockSharePointSource>();
            services.AddSingleton<ISharePointSource>(sp => sp.GetRequiredService<MockSharePointSource>());
        }

        else if (options.Mode == SharePointMode.Graph)
        {
            services.AddSingleton<IAuthorizationKeyWrapper>(_ => options.AuthKeyUri is { } keyUri
                ? new KeyVaultAuthorizationKeyWrapper(keyUri)
                : new LocalAuthorizationKeyWrapper(options.AuthDevKeyPath));
            services.AddScoped<IRequestAuthorizationStore, RequestAuthorizationStore>();

            services.AddSingleton<ISharePointAccessContext, SharePointAccessContext>();
            services.AddSingleton<ISharePointSource>(sp => new GraphSharePointSource(
                new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
                { Timeout = TimeSpan.FromMinutes(2) },
                sp.GetRequiredService<ISharePointAccessContext>(),
                options));
        }
        else
        {
            services.AddSingleton<ISharePointSource, DisabledSharePointSource>();
        }

        // Handlers resolve in every mode; the gate inside them answers 503 when the feature is off.
        services.AddSingleton<SourceLinkClassifier>();
        services.AddScoped<EligibilityEvaluator>();
        services.AddScoped<DiscoveryStepService>();
        services.AddScoped<BulkScoringAccess>();
        var admissionHmacKey = RandomNumberGenerator.GetBytes(32);
        services.AddScoped(sp => new BulkSourceAdmissionService(
            sp.GetRequiredService<IBulkSourceAdmissionRepository>(),
            options,
            admissionHmacKey,
            sp.GetRequiredService<TimeProvider>()));
        if (options.Enabled)
        {
            services.AddScoped<ISourceConnectionWorkflow, SourceConnectionWorkflow>();
            services.AddScoped<PendingSourceAuthorizationStore>();
            services.AddSingleton<ISourceAuthorizationCodeClient, MsalSourceAuthorizationCodeClient>();
            services.AddSingleton<IClientAssertionProvider, ManagedIdentityClientAssertion>();
        }
        else
        {
            services.AddScoped<ISourceConnectionWorkflow, DisabledSourceConnectionWorkflow>();
        }

        services.AddScoped<BulkTelemetry>();
        services.AddSingleton<SourceAuthorizationTelemetry>();
        services.AddScoped<IBulkContentStore, BulkContentStore>();

        if (options.Enabled)
        {
            services.TryAddSingleton<IDocumentRasterizer, PdfiumRasterizer>();
            services.AddScoped<BulkItemProcessor>();
            services.AddScoped<BulkAuthorizationLapseService>();
            services.AddScoped<BulkAuthorizationCleanupService>();
            services.AddScoped<BulkScoringCycle>();
            services.TryAddSingleton<DatabaseInitializationGate>();
            services.AddHostedService<BulkScoringWorker>();
        }

        return services;
    }
}
