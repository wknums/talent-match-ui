using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Tests;

public sealed class SimpleAuthModeTests : IClassFixture<SimpleAuthModeTests.SimpleFactory>
{
    private readonly SimpleFactory _factory;

    public SimpleAuthModeTests(SimpleFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LocalPasswordLoginAndUserManagementRemainAvailable()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Username = "admin",
            Password = "adm1n99",
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var usersResponse = await client.GetAsync("/api/users");
        usersResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var changePasswordResponse = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            CurrentPassword = "adm1n99",
            NewPassword = "new-password99",
        });
        changePasswordResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("GET", "/api/access-management/users")]
    [InlineData("POST", "/api/organizations")]
    public async Task EntraAdministrationEndpointsAreUnavailable(string method, string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new
            {
                Name = "Unavailable Organization",
                FirstDepartmentName = "Engineering",
            });
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    public sealed class SimpleFactory : WebApplicationFactory<Program>
    {
        private SqliteConnection? _connection;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("APP_AUTH_MODE", "simple");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _connection?.Dispose();
        }
    }
}