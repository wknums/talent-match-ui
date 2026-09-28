using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using TalentMatch.Application.Stats.Queries;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Endpoints;

namespace TalentMatch.Web.Tests;

public sealed class ScoringThroughputEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Get_ReturnsAuthenticatedNonCachedThroughput(bool authenticated)
    {
        var asOf = new DateTimeOffset(2026, 9, 17, 7, 32, 7, TimeSpan.Zero);
        var hours = Enumerable.Range(0, 24).Select(index => new ScoringThroughputBucketDto(
            asOf.AddHours(index - 24), asOf.AddHours(index - 23), index == 23 ? 7 : 0)).ToArray();
        var sender = new Mock<ISender>();
        sender.Setup(value => value.Send(It.IsAny<GetScoringThroughputQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScoringThroughputDto(asOf, 7, 7, hours));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(sender.Object);
        builder.Services.AddSingleton(Mock.Of<IProcessingEventRepository>());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStatsEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        if (authenticated)
            client.DefaultRequestHeaders.Add("X-Test-User", "user-1");

        using var response = await client.GetAsync("/api/stats/scoring-throughput");

        response.StatusCode.Should().Be(authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        if (authenticated)
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("asOfUtc").GetDateTimeOffset().Should().Be(asOf);
            body.GetProperty("scoredLastHour").GetInt32().Should().Be(7);
            body.GetProperty("hours").GetArrayLength().Should().Be(24);
            body.GetProperty("hours")[23].GetProperty("count").GetInt32().Should().Be(7);
        }
        else
        {
            sender.Verify(value => value.Send(
                It.IsAny<GetScoringThroughputQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private sealed class HeaderAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.ContainsKey("X-Test-User")
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], Scheme.Name)),
                    Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
