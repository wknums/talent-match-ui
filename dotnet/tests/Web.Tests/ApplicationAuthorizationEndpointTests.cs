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
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Endpoints;

namespace TalentMatch.Web.Tests;

public sealed class ApplicationAuthorizationEndpointTests
{
    [Fact]
    public async Task GetApplication_OutsideCurrentScope_ReturnsForbidden()
    {
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(repo => repo.GetByIdAsync("application-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application
            {
                Id = "application-1",
                JobId = "job-1",
            });
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repo => repo.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job
            {
                Id = "job-1",
                OrganizationId = Guid.NewGuid().ToString(),
                DepartmentId = Guid.NewGuid().ToString(),
            });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(service => service.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentAuthorizationState(
                new CurrentEntraClaims(
                    "tenant", "user", "user", "User", "user@example.com",
                    DateTimeOffset.UtcNow, new HashSet<string>(), new HashSet<string>()),
                null, [], [], [], [], "tenant", null));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(applications.Object);
        builder.Services.AddSingleton(jobs.Object);
        builder.Services.AddSingleton(currentUser.Object);
        builder.Services.AddSingleton(Mock.Of<ISender>());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapApplicationsEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/applications/application-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed class TestAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "user")], Scheme.Name)),
                Scheme.Name)));
    }
}
