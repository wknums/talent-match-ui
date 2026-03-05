using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.Services;

namespace TalentMatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        var provider = configuration["DatabaseProvider"] ?? "sqlite";

        if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(connectionString));
        }
        else
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(connectionString ?? "Data Source=talentmatch.db"));
        }

        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IApplicationRepository, ApplicationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IProcessingEventRepository, ProcessingEventRepository>();
        services.AddScoped<IFailureQueueRepository, FailureQueueRepository>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddHttpContextAccessor();

        return services;
    }
}
