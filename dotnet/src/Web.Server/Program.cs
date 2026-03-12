using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Application;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Web.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

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

// Ensure database is created and seed default admin
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (app.Environment.IsEnvironment("Testing"))
        db.Database.EnsureCreated();
    else
        db.Database.Migrate();

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
