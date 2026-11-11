using System.Net;
using System.Net.Http.Headers;
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
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Endpoints;

namespace TalentMatch.Web.Tests;

public sealed class ApplicationUploadEndpointsTests
{
    private const int MaxFileBytes = 15 * 1024 * 1024;

    [Theory]
    [InlineData("candidate.exe", "application/pdf", 1, HttpStatusCode.BadRequest)]
    [InlineData("candidate.zip", "application/octet-stream", 1, HttpStatusCode.BadRequest)]
    [InlineData("candidate.pdf", "application/pdf", MaxFileBytes + 1, HttpStatusCode.RequestEntityTooLarge)]
    public async Task Upload_rejects_invalid_files_without_dispatching(
        string fileName, string mimeType, int size, HttpStatusCode expectedStatus)
    {
        var sender = new Mock<ISender>();
        await using var app = await CreateAppAsync(sender);
        using var client = app.GetTestClient();
        using var form = CreateForm(fileName, mimeType, size);

        using var response = await client.PostAsync("/api/jobs/job-1/applications/upload", form);

        response.StatusCode.Should().Be(expectedStatus);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        sender.Verify(x => x.Send(
            It.IsAny<UploadApplicationsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("candidate.pdf", "application/pdf", MaxFileBytes)]
    [InlineData("candidate.md", "text/markdown", 1)]
    [InlineData("candidate.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 1)]
    [InlineData("candidate.txt", "text/plain", 1)]
    [InlineData("candidate.jpg", "image/jpeg", 1)]
    [InlineData("candidate.png", "image/png", 1)]
    public async Task Upload_accepts_supported_files_including_the_exact_size_limit(
        string fileName, string mimeType, int size)
    {
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(It.IsAny<UploadApplicationsCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        await using var app = await CreateAppAsync(sender);
        using var client = app.GetTestClient();
        using var form = CreateForm(fileName, mimeType, size);

        using var response = await client.PostAsync("/api/jobs/job-1/applications/upload?allowDuplicates=true", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sender.Verify(x => x.Send(
            It.Is<UploadApplicationsCommand>(command => command.AllowDuplicates
                && command.Files.Count == 1
                && command.Files[0].FileName == fileName
                && command.Files[0].FileType == mimeType
                && command.Files[0].FileSize == size),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_rejects_the_entire_batch_if_a_later_file_is_invalid()
    {
        var sender = new Mock<ISender>();
        await using var app = await CreateAppAsync(sender);
        using var client = app.GetTestClient();
        using var form = CreateForm("candidate.pdf", "application/pdf", 1);
        form.Add(new ByteArrayContent([1]), "files", "candidate.exe");

        using var response = await client.PostAsync("/api/jobs/job-1/applications/upload", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        sender.Verify(x => x.Send(
            It.IsAny<UploadApplicationsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MultipartFormDataContent CreateForm(string fileName, string mimeType, int size)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[size]);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mimeType);
        form.Add(file, "files", fileName);
        return form;
    }

    private static async Task<WebApplication> CreateAppAsync(Mock<ISender> sender)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(sender.Object);
        builder.Services.AddSingleton(Mock.Of<IApplicationRepository>());
        builder.Services.AddSingleton(Mock.Of<IJobRepository>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapApplicationsEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "user")], Scheme.Name)),
                Scheme.Name)));
    }
}
