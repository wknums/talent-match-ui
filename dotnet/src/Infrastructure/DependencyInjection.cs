using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.Services;

namespace TalentMatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var provider = configuration["DatabaseProvider"] ?? "sqlite";

        if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = BuildResilientSqlServerConnectionString(ResolveSqlServerConnectionString(configuration));
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(connectionString, sqlOptions =>
                {
                    // Cold-start aware retry profile for Azure SQL pay-as-you-go wake-up windows.
                    sqlOptions.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(15), errorNumbersToAdd: null);
                    sqlOptions.CommandTimeout(180);
                }));
        }
        else
        {
            var sqliteConnectionString = ResolveSqliteConnectionString(configuration, contentRootPath);
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(sqliteConnectionString));
        }

        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IApplicationRepository, ApplicationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IEntraAccessManagementRepository, EntraAccessManagementRepository>();
        services.AddScoped<IProcessingEventRepository, ProcessingEventRepository>();
        services.AddScoped<INavigationAuditRepository, NavigationAuditRepository>();
        services.AddScoped<IFailureQueueRepository, FailureQueueRepository>();
        services.AddScoped<IScoringPromptRepository, ScoringPromptRepository>();
        services.AddScoped<IPromptTestRunRepository, PromptTestRunRepository>();
        services.AddScoped<IScoringBatchRepository, ScoringBatchRepository>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddTransient<AwrAuthHandler>();
        services.AddHttpClient<ILlmProxyService, LlmProxyService>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(6);
            })
            .AddHttpMessageHandler<AwrAuthHandler>();
        services.AddHttpClient("AwrApiClient", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(6);
            })
            .AddHttpMessageHandler<AwrAuthHandler>();
        services.AddHttpClient<IPlatformScoringService, PlatformScoringService>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(6);
            })
            .AddHttpMessageHandler<AwrAuthHandler>();
        services.AddHttpContextAccessor();

        // Blob store: AzureBlobStore when AWR_BLOB_STORAGE_ACCOUNT is set (platform mode),
        // otherwise InlineBlobStore (sequential / dev). See specs/008-platform-mode-shift/platform-contract.md.
        services.AddSingleton<IBlobStore>(sp =>
        {
            var account = Environment.GetEnvironmentVariable("AWR_BLOB_STORAGE_ACCOUNT");
            if (string.IsNullOrWhiteSpace(account))
            {
                return new InlineBlobStore();
            }
            var container = Environment.GetEnvironmentVariable("AWR_BLOB_CONTAINER");
            if (string.IsNullOrWhiteSpace(container)) container = "cv-uploads";
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AzureBlobStore>();
            return new AzureBlobStore(account, container, logger);
        });

        // Platform-mode reconciler hosted service. It self-disables when scoring
        // mode is sequential, so it is safe to register unconditionally.
        services.AddHostedService<TalentMatch.Infrastructure.HostedServices.PlatformScoringReconciler>();

        return services;
    }

    private static string BuildResilientSqlServerConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString ?? string.Empty;
        }

        var builder = new SqlConnectionStringBuilder(connectionString);

        // Ensure connection open waits long enough for Azure SQL cold-start wake-up.
        if (builder.ConnectTimeout < 90)
        {
            builder.ConnectTimeout = 90;
        }

        return builder.ConnectionString;
    }

    private static string? ResolveSqlServerConnectionString(IConfiguration configuration)
    {
        var configuredConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        var authMode = configuration["AZURE_SQL_AUTH_MODE"];
        var server = configuration["AZURE_SQL_SERVER_FQDN"];
        var database = configuration["AZURE_SQL_DATABASE_NAME"];
        if (!string.Equals(authMode, "entra", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database)))
        {
            throw new InvalidOperationException("DatabaseProvider=sqlserver requires either ConnectionStrings__DefaultConnection or the Entra settings AZURE_SQL_AUTH_MODE=entra, AZURE_SQL_SERVER_FQDN, and AZURE_SQL_DATABASE_NAME.");
        }

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException("AZURE_SQL_SERVER_FQDN and AZURE_SQL_DATABASE_NAME are required when Azure SQL Entra authentication is enabled.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = true,
            TrustServerCertificate = false,
            Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity,
        };

        var clientId = configuration["AZURE_CLIENT_ID"];
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            builder.UserID = clientId;
        }

        return builder.ConnectionString;
    }

    private static string ResolveSqliteConnectionString(IConfiguration configuration, string contentRootPath)
    {
        var configuredConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return NormalizeSqliteConnectionString(configuredConnectionString, contentRootPath);
        }

        var configuredPath = configuration["SQLITE_DB_PATH"];
        var dbPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "..", "shared-data", "talentmatch.db"))
            : Path.GetFullPath(Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(contentRootPath, configuredPath));

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return $"Data Source={dbPath}";
    }

    private static string NormalizeSqliteConnectionString(string connectionString, string contentRootPath)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            return connectionString;
        }

        var resolvedPath = Path.GetFullPath(Path.IsPathRooted(builder.DataSource)
            ? builder.DataSource
            : Path.Combine(contentRootPath, builder.DataSource));

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);
        builder.DataSource = resolvedPath;
        return builder.ToString();
    }
}
