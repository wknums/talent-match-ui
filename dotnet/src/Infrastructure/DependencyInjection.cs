using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(connectionString));
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
        services.AddScoped<IProcessingEventRepository, ProcessingEventRepository>();
        services.AddScoped<IFailureQueueRepository, FailureQueueRepository>();
        services.AddScoped<IScoringPromptRepository, ScoringPromptRepository>();
        services.AddScoped<IPromptTestRunRepository, PromptTestRunRepository>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddTransient<AwrAuthHandler>();
        services.AddHttpClient<ILlmProxyService, LlmProxyService>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
            })
            .AddHttpMessageHandler<AwrAuthHandler>();
        services.AddHttpClient("AwrApiClient", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
            })
            .AddHttpMessageHandler<AwrAuthHandler>();
        services.AddHttpContextAccessor();

        return services;
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
