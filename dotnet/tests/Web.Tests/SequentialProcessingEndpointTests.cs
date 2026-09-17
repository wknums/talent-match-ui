using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Application.Jobs.Queries;
using TalentMatch.Application.Prompts.Queries;
using TalentMatch.Application.Rubrics.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Endpoints;

namespace TalentMatch.Web.Tests;

public sealed class SequentialProcessingEndpointTests
{
    [Fact]
    public async Task Process_ReturnsAcceptedWithQueuedCount_WithoutWaitingForScoring()
    {
        var previousEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");
        Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", null);
        try
        {
            var job = new Job
            {
                Id = "job-1",
                CurrentConfigVersionId = "config-1",
                ConfigVersions = [new JobConfigVersion { Id = "config-1", RubricApprovalStatus = "approved" }],
            };
            var jobs = new Mock<IJobRepository>();
            var applications = new Mock<IApplicationRepository>();
            var signal = new Mock<IScoringQueueSignal>();
            jobs.Setup(repo => repo.GetByIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(job);
            applications.Setup(repo => repo.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Enumerable.Range(1, 7)
                    .Select(i => new Domain.Entities.Application { Id = $"app-{i}", JobId = "job-1", Status = "Queued" })
                    .ToArray());
            var handler = new ProcessJobCommandHandler(
                jobs.Object, applications.Object, Mock.Of<IScoringBatchRepository>(),
                Mock.Of<IServiceScopeFactory>(), NullLogger<ProcessJobCommandHandler>.Instance,
                queueSignal: signal.Object);
            var mediator = new Mock<ISender>();
            mediator.Setup(sender => sender.Send(It.IsAny<GetPromptsQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new ScoringPrompt { Id = "prompt-1", Status = "production-approved" } });
            mediator.Setup(sender => sender.Send(It.IsAny<GetJobDetailQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(job);
            mediator.Setup(sender => sender.Send(It.IsAny<ProcessJobCommand>(), It.IsAny<CancellationToken>()))
                .Returns((ProcessJobCommand command, CancellationToken ct) => handler.Handle(command, ct));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(mediator.Object);
            builder.Services.AddSingleton(Mock.Of<IScoringBatchRepository>());
            builder.Services.AddScoped<LegacyRubricAdapter>();
            builder.Services.AddScoped<RubricOrderNormalizer>();
            await using var app = builder.Build();
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "test-user")], "test"));
                return next(context);
            });
            app.UseAuthorization();
            app.MapJobsEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            using var response = await client.PostAsync("/api/jobs/job-1/process", null);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            response.Headers.Location!.OriginalString.Should().Be("/api/jobs/job-1/applications");
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            result.GetProperty("queued").GetInt32().Should().Be(7);
            result.GetProperty("processed").GetInt32().Should().Be(0);
            signal.Verify(value => value.Pulse(), Times.Once);
            applications.Verify(repo => repo.UpdateAsync(
                It.IsAny<Domain.Entities.Application>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", previousEndpoint);
        }
    }
}
