using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Queries;
using TalentMatch.Web.Server.Endpoints;

namespace TalentMatch.Web.Tests;

public sealed class UploadSettingsEndpointsTests
{
    [Theory]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("recruiter", HttpStatusCode.Forbidden)]
    [InlineData("organization_admin", HttpStatusCode.Forbidden)]
    public async Task Get_RequiresGlobalAdminRole(string role, HttpStatusCode expected)
    {
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(It.IsAny<GetUploadSettingsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadSettingsDto(4, 4_194_304, 104_857_600, 0, false, null, null));
        await using var app = await CreateAppAsync(sender.Object, role);
        using var response = await app.GetTestClient().GetAsync("/api/admin/upload-settings");
        response.StatusCode.Should().Be(expected);
    }

    private static async Task<WebApplication> CreateAppAsync(ISender sender, string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(role);
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin")));
        builder.Services.AddSingleton(sender);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapUploadSettingsEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        string role)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "user-1"),
                        new Claim(ClaimTypes.Role, role),
                    ],
                    Scheme.Name,
                    ClaimTypes.NameIdentifier,
                    ClaimTypes.Role)),
                Scheme.Name)));
    }
}
