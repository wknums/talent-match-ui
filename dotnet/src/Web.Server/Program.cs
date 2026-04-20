using System.Security.Claims;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TalentMatch.Application;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Services;
using TalentMatch.Web.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

// Auth - using cookie-based auth for demo, Entra ID for production
builder.Services.AddAuthentication("cookie")
    .AddCookie("cookie", options =>
    {
        options.LoginPath = "/api/auth/login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "TalentMatch API", Version = "v1" });
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(b => b
        .SetIsOriginAllowed(_ => true)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});

var app = builder.Build();

// Validate AWReason API auth configuration at startup
AwrAuthHandler.ValidateConfiguration();

// Ensure database is created and seed default admin
await ExecuteWithSqlWarmupRetryAsync(async () =>
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (app.Environment.IsEnvironment("Testing"))
    {
        db.Database.EnsureCreated();
    }
    else if (db.Database.IsSqlite())
    {
        EnsureSharedSqliteSchemaIfNeeded(db, app.Environment.ContentRootPath);
        BaselineSharedSqliteSchemaIfNeeded(db);
    }
    else
    {
        db.Database.Migrate();
    }

    // Seed default admin user if no users exist (matches Stack A init-users.ts / AUTHENTICATION.md)
    if (!db.Users.Any())
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("adm1n99")));
        db.Users.Add(new User
        {
            Username = "admin",
            Role = "admin",
            FullName = "Administrator",
            Department = "all",
            PasswordHash = hash
        });
        db.SaveChanges();
    }
});

static async Task ExecuteWithSqlWarmupRetryAsync(Func<Task> operation)
{
    const int maxAttempts = 8;
    const int initialDelayMs = 2000;
    const int maxDelayMs = 15000;

    var startedAt = DateTimeOffset.UtcNow;
    Exception? lastError = null;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await operation();

            if (attempt > 1)
            {
                var elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
                Console.WriteLine($"[startup] Azure SQL operation succeeded after retry (attempt={attempt}, elapsedMs={elapsedMs})");
            }

            return;
        }
        catch (Exception ex) when (IsTransientSqlWarmupError(ex) && attempt < maxAttempts)
        {
            lastError = ex;
            var delayMs = Math.Min(maxDelayMs, initialDelayMs * (int)Math.Pow(2, attempt - 1));
            var elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

            Console.WriteLine($"[startup] Azure SQL retry scheduled (attempt={attempt}/{maxAttempts}, elapsedMs={elapsedMs}, delayMs={delayMs}, error=\"{ex.Message}\")");
            await Task.Delay(delayMs);
        }
        catch (Exception ex)
        {
            lastError = ex;
            break;
        }
    }

    var totalElapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
    Console.WriteLine($"[startup] Azure SQL startup operation failed after retries (attempts={maxAttempts}, elapsedMs={totalElapsedMs}, error=\"{lastError?.Message}\")");
    throw lastError ?? new InvalidOperationException("Azure SQL startup operation failed without an exception.");
}

static bool IsTransientSqlWarmupError(Exception ex)
{
    if (ex is TimeoutException)
    {
        return true;
    }

    if (ex is SqlException sqlEx)
    {
        var transientErrorNumbers = new HashSet<int>
        {
            40501, // Service busy
            40613, // Database unavailable
            40197, // Service encountered an error
            10928, // Resource limit reached
            10929, // Resource limit reached
            49918,
            49919,
            49920,
            -2,    // Client-side timeout
        };

        foreach (SqlError error in sqlEx.Errors)
        {
            if (transientErrorNumbers.Contains(error.Number))
            {
                return true;
            }
        }
    }

    if (ex is InvalidOperationException)
    {
        var msg = ex.Message.ToLowerInvariant();
        if (msg.Contains("timeout") || msg.Contains("transient") || msg.Contains("temporarily"))
        {
            return true;
        }
    }

    return ex.InnerException is not null && IsTransientSqlWarmupError(ex.InnerException);
}

static bool BaselineSharedSqliteSchemaIfNeeded(AppDbContext db)
{
    string[] knownMigrationIds =
    [
        "20260305150916_InitialCreate",
        "20260306155432_AddDesiredCriteriaAndJobDescription",
        "20260309180812_AddScoringPromptAndPromptTestRun",
        "20260310120500_AddCreatedByToJob",
        "20260312120000_AddRubricApprovalStatus",
        "20260313150938_AddRubricSourceToJobConfigVersion",
        "20260323182030_AddLastErrorToApplication",
    ];

    if (!db.Database.IsSqlite())
        return false;

    var connection = (SqliteConnection)db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;
    if (shouldClose)
        connection.Open();

    try
    {
        using var hasHistoryTableCommand = connection.CreateCommand();
        hasHistoryTableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory' AND type = 'table';";
        var hasHistoryTable = Convert.ToInt32(hasHistoryTableCommand.ExecuteScalar()) > 0;
        if (hasHistoryTable)
        {
            using var historyRowCountCommand = connection.CreateCommand();
            historyRowCountCommand.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";";
            var historyRowCount = Convert.ToInt32(historyRowCountCommand.ExecuteScalar());
            if (historyRowCount > 0)
                return false;
        }

        using var hasExistingSchemaCommand = connection.CreateCommand();
        hasExistingSchemaCommand.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('Users', 'Jobs', 'Applications', 'ScoringRuns', 'AggregatedResults', 'FailureQueueItems');";
        var hasExistingSchema = Convert.ToInt32(hasExistingSchemaCommand.ExecuteScalar()) > 0;
        if (!hasExistingSchema)
            return false;

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                ""ProductVersion"" TEXT NOT NULL
            );");

        var productVersion = typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+')[0]
            ?? "10.0.0";

                foreach (var migrationId in knownMigrationIds)
        {
            db.Database.ExecuteSqlRaw(
                @"INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                  VALUES ({0}, {1});",
                migrationId,
                productVersion);
        }

                using var appliedHistoryCountCommand = connection.CreateCommand();
                appliedHistoryCountCommand.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";";
                return Convert.ToInt32(appliedHistoryCountCommand.ExecuteScalar()) > 0;
    }
    finally
    {
        if (shouldClose)
            connection.Close();
    }
}

static void EnsureSharedSqliteSchemaIfNeeded(AppDbContext db, string contentRootPath)
{
    if (!db.Database.IsSqlite())
        return;

    var connection = (SqliteConnection)db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;
    if (shouldClose)
        connection.Open();

    try
    {
        using var hasExistingSchemaCommand = connection.CreateCommand();
        hasExistingSchemaCommand.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('Users', 'Jobs', 'Applications', 'ScoringRuns', 'AggregatedResults', 'FailureQueueItems');";
        var hasExistingSchema = Convert.ToInt32(hasExistingSchemaCommand.ExecuteScalar()) > 0;
        if (hasExistingSchema)
            return;

        var schemaPath = Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "..", "server", "storage", "schema-sqlite.sql"));
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException($"Shared SQLite schema file not found: {schemaPath}");

        using var initializeSchemaCommand = connection.CreateCommand();
        initializeSchemaCommand.CommandText = File.ReadAllText(schemaPath);
        initializeSchemaCommand.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
            connection.Close();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Map endpoints
app.MapAuthEndpoints();
app.MapUsersEndpoints();
app.MapJobsEndpoints();
app.MapApplicationsEndpoints();
app.MapStatsEndpoints();
app.MapDlqEndpoints();
app.MapPromptEndpoints();
app.MapAnalyticsEndpoints();

app.MapFallbackToFile("index.html");

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
