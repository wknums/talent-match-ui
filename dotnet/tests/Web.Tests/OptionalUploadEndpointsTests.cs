using System.Net;
using System.Net.Http.Json;
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
using TalentMatch.Application.Uploads.Commands;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Queries;
using TalentMatch.Web.Server.Endpoints;

namespace TalentMatch.Web.Tests;

public sealed class OptionalUploadEndpointsTests
{
    [Fact]
    public async Task CreateSession_UsesContractRouteAndReturnsCreated()
    {
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(
                It.IsAny<CreateUploadSessionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session());
        await using var app = await CreateAppAsync(sender.Object);
        using var client = app.GetTestClient();
        var request = new CreateUploadSessionRequest(false,
            [new(Guid.NewGuid(), 0, "candidate.pdf", "application/pdf", 10)]);

        using var response = await client.PostAsJsonAsync(
            "/api/jobs/job-1/upload-sessions", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be("/api/upload-sessions/session-1");
        response.Headers.Should().ContainKey("X-Correlation-ID");
    }

    [Fact]
    public async Task Content_RejectsMultipartWithoutExactlyOneFile()
    {
        await using var app = await CreateAppAsync(Mock.Of<ISender>());
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/upload-sessions/session-1/items/item-1/content");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = new MultipartFormDataContent();

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListOwnedSessions_ReturnsCanonicalSummaries()
    {
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(
                It.IsAny<ListOwnedUploadSessionsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([ToSummary(Session())]);
        await using var app = await CreateAppAsync(sender.Object);
        using var client = app.GetTestClient();

        var result = await client.GetFromJsonAsync<List<UploadSessionSummaryDto>>(
            "/api/upload-sessions?jobId=job-1&includeTerminal=true");

        result.Should().ContainSingle().Which.Id.Should().Be("session-1");
    }

    private static async Task<WebApplication> CreateAppAsync(ISender sender)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(sender);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapUploadEndpoints();
        await app.StartAsync();
        return app;
    }

    private static UploadSessionDetailDto Session()
    {
        var now = DateTime.UtcNow;
        return new(
            "session-1", "job-1", "active", false,
            new(4, 4_194_304, 104_857_600),
            new(1, 1, 0, 0, 0, 0, 0, 0),
            0, "correlation-1", now, null, now, null, 1, []);
    }

    private static UploadSessionSummaryDto ToSummary(UploadSessionDetailDto detail) => new(
        detail.Id, detail.JobId, detail.Status, detail.AllowDuplicates, detail.Limits,
        detail.Counts, detail.ProgressPercent, detail.CorrelationId, detail.CreatedAt,
        detail.StartedAt, detail.LastHeartbeatAt, detail.CompletedAt, detail.ConcurrencyVersion);

    private sealed class TestAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                    Scheme.Name,
                    ClaimTypes.NameIdentifier,
                    ClaimTypes.Role)),
                Scheme.Name)));
    }
}
