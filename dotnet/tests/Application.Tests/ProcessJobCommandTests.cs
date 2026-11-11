using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class ProcessJobCommandTests
{
    [Fact]
    public async Task Handle_Sequential_OnlySignalsQueuedProductionWork_WithoutStartingAnotherPool()
    {
        var originalPlatformEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");
        Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", null);
        try
        {
            var jobs = new Mock<IJobRepository>();
            var applications = new Mock<IApplicationRepository>();
            var batches = new Mock<IScoringBatchRepository>(MockBehavior.Strict);
            var signal = new Mock<IScoringQueueSignal>();
            jobs.Setup(repo => repo.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Job
                {
                    Id = "job-1",
                    CurrentConfigVersionId = "config-1",
                    ConfigVersions = [new JobConfigVersion { Id = "config-1", RubricApprovalStatus = "approved" }],
                });
            applications.Setup(repo => repo.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    new Domain.Entities.Application { Id = "queued", Status = "Queued" },
                    new Domain.Entities.Application { Id = "active", Status = "Scoring" },
                    new Domain.Entities.Application { Id = "failed", Status = "ScoringFailed" },
                    new Domain.Entities.Application { Id = "test", Status = "Queued", TestRunId = "test-1" },
                ]);
            var handler = new ProcessJobCommandHandler(jobs.Object, applications.Object, batches.Object,
                Mock.Of<IServiceScopeFactory>(), Mock.Of<ILogger<ProcessJobCommandHandler>>(), queueSignal: signal.Object);

            var first = await handler.Handle(new ProcessJobCommand("job-1", "prompt-1", 3), CancellationToken.None);
            var second = await handler.Handle(new ProcessJobCommand("job-1", "prompt-1", 3), CancellationToken.None);

            first.Processed.Should().Be(0);
            first.Queued.Should().Be(1);
            second.Queued.Should().Be(1);
            signal.Verify(x => x.Pulse(), Times.Exactly(2));
            applications.Verify(
                repo => repo.UpdateAsync(It.IsAny<Domain.Entities.Application>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", originalPlatformEndpoint);
        }
    }

    [Fact]
    public async Task Handle_DoesNotCreateAnotherBatchWhenAllApplicationsAreAlreadyActive()
    {
        var originalSequentialEndpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
        var originalPlatformEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");
        Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", "https://sequential.test/api");
        Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", "https://platform.test/api");

        try
        {
            var jobs = new Mock<IJobRepository>();
            var applications = new Mock<IApplicationRepository>();
            var batches = new Mock<IScoringBatchRepository>();
            var applicationIds = Enumerable.Range(1, 7).Select(i => $"active-{i}").ToArray();

            jobs.Setup(x => x.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Job { Id = "job-1", Title = "Test Job" });
            applications.Setup(x => x.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(applicationIds.Select(id =>
                    new Domain.Entities.Application { Id = id, JobId = "job-1", Status = "Scoring" }).ToArray());
            batches.Setup(x => x.ListByJobAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    new ScoringBatch
                    {
                        JobId = "job-1",
                        Status = "submitted",
                        ApplicationIdsJson = JsonSerializer.Serialize(applicationIds),
                    },
                ]);
            var handler = new ProcessJobCommandHandler(
                jobs.Object,
                applications.Object,
                batches.Object,
                Mock.Of<IServiceScopeFactory>(),
                Mock.Of<ILogger<ProcessJobCommandHandler>>());

            var result = await handler.Handle(
                new ProcessJobCommand("job-1", "prompt-1", 3),
                CancellationToken.None);

            result.Processed.Should().Be(0);
            result.Errors.Should().BeEmpty();
            batches.Verify(
                x => x.CreateAsync(It.IsAny<ScoringBatch>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", originalSequentialEndpoint);
            Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", originalPlatformEndpoint);
        }
    }

    [Fact]
    public async Task Handle_EnqueuesOnlyNewApplicationsWhileAnotherBatchIsActive()
    {
        var originalSequentialEndpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
        var originalPlatformEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");
        var originalBatchSize = Environment.GetEnvironmentVariable("AWR_PLATFORM_BATCH_SIZE");
        Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", "https://sequential.test/api");
        Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", "https://platform.test/api");
        Environment.SetEnvironmentVariable("AWR_PLATFORM_BATCH_SIZE", "7");

        try
        {
            var jobs = new Mock<IJobRepository>();
            var applications = new Mock<IApplicationRepository>();
            var batches = new Mock<IScoringBatchRepository>();
            var firstUploadIds = Enumerable.Range(1, 7).Select(i => $"existing-{i}").ToArray();
            var secondUploadIds = Enumerable.Range(1, 7).Select(i => $"new-{i}").ToArray();
            var allApplications = firstUploadIds
                .Select(id => new Domain.Entities.Application { Id = id, JobId = "job-1", Status = "Scoring" })
                .Concat(secondUploadIds.Select(id =>
                    new Domain.Entities.Application { Id = id, JobId = "job-1", Status = "Queued" }))
                .ToArray();

            jobs.Setup(x => x.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Job { Id = "job-1", Title = "Test Job" });
            applications.Setup(x => x.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(allApplications);
            applications.Setup(x => x.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => allApplications.Single(app => app.Id == id));
            batches.Setup(x => x.ListByJobAsync("job-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    new ScoringBatch
                    {
                        JobId = "job-1",
                        Status = "submitted",
                        ApplicationIdsJson = JsonSerializer.Serialize(firstUploadIds),
                    },
                ]);
            batches.Setup(x => x.CreateAsync(It.IsAny<ScoringBatch>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ScoringBatch batch, CancellationToken _) => batch);

            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider.Setup(x => x.GetService(typeof(IScoringBatchRepository)))
                .Returns(batches.Object);
            serviceProvider.Setup(x => x.GetService(typeof(IApplicationRepository)))
                .Returns(applications.Object);
            var scope = new Mock<IServiceScope>();
            scope.SetupGet(x => x.ServiceProvider).Returns(serviceProvider.Object);
            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(x => x.CreateScope()).Returns(scope.Object);
            var handler = new ProcessJobCommandHandler(
                jobs.Object,
                applications.Object,
                batches.Object,
                scopeFactory.Object,
                Mock.Of<ILogger<ProcessJobCommandHandler>>());

            var result = await handler.Handle(
                new ProcessJobCommand("job-1", "prompt-1", 3),
                CancellationToken.None);

            result.Processed.Should().Be(7);
            result.Total.Should().Be(14);
            result.Errors.Should().BeEmpty();
            batches.Verify(
                x => x.CreateAsync(
                    It.Is<ScoringBatch>(batch =>
                        JsonSerializer.Deserialize<string[]>(batch.ApplicationIdsJson)!.SequenceEqual(secondUploadIds)),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            batches.Verify(
                x => x.AddProgressAsync("job-1", 7, 1, It.IsAny<CancellationToken>()),
                Times.Once);
            batches.Verify(
                x => x.InitProgressAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);
            applications.Verify(
                x => x.UpdateAsync(
                    It.Is<Domain.Entities.Application>(app =>
                        secondUploadIds.Contains(app.Id) && app.Status == "Scoring"),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(7));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWR_SEQ_API_ENDPOINT", originalSequentialEndpoint);
            Environment.SetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT", originalPlatformEndpoint);
            Environment.SetEnvironmentVariable("AWR_PLATFORM_BATCH_SIZE", originalBatchSize);
        }
    }
}
