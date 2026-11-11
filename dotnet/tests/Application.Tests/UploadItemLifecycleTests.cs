using System.Net;
using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Uploads.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class UploadItemLifecycleTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void RetryClassification_IsNarrow(HttpStatusCode? status, bool expected) =>
        UploadItemLifecycleService.IsTransient(status).Should().Be(expected);

    [Fact]
    public void TransientFourthFailure_BecomesTerminal()
    {
        var service = new UploadItemLifecycleService();
        var item = new UploadItem { Status = UploadItemStatus.Uploading, AttemptCount = 4 };
        service.RecordFailure(item, HttpStatusCode.ServiceUnavailable, DateTime.UtcNow);
        item.Status.Should().Be(UploadItemStatus.Failed);
        item.OutcomeCode.Should().Be("retry_exhausted");
    }

    [Fact]
    public void CapacityWaiting_NeverFails()
    {
        var service = new UploadItemLifecycleService();
        var item = new UploadItem();
        service.MarkClientStatus(item, UploadItemStatus.Throttled, DateTime.UtcNow);
        item.Status.Should().Be(UploadItemStatus.Throttled);
        item.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task ClientTransportExhaustion_RequiresFourAttemptsAndPersistsFailed()
    {
        var occurrence = Guid.NewGuid();
        var item = new UploadItem
        {
            Id = "item-1",
            SessionId = "session-1",
            OccurrenceKey = occurrence.ToString(),
        };
        var session = new UploadSession
        {
            Id = "session-1",
            JobId = "job-1",
            OwnerActorId = "owner-1",
        };
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(x => x.GetOwnedAsync(
                "session-1", "owner-1", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(x => x.GetItemAsync(
                "session-1", "item-1", "owner-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        sessions.Setup(x => x.UpdateItemAsync(
                It.IsAny<UploadItem>(),
                1,
                It.IsAny<IReadOnlyCollection<ProcessingEvent>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadItem updated, int _, IReadOnlyCollection<ProcessingEvent> _, CancellationToken _) => updated);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var service = new UploadItemLifecycleService(sessions.Object, currentUser.Object);

        var result = await service.UpdateClientStatusAsync(
            "session-1",
            "item-1",
            new(
                occurrence,
                "failed",
                1,
                "retry_exhausted",
                "Upload failed after four browser transport attempts.",
                TransportAttemptCount: 4),
            "correlation-1",
            CancellationToken.None);

        result.Status.Should().Be("failed");
        result.OutcomeCode.Should().Be("retry_exhausted");
        result.CompletedAt.Should().NotBeNull();
        sessions.Verify(x => x.UpdateItemAsync(
            It.Is<UploadItem>(updated => updated.Status == UploadItemStatus.Failed),
            1,
            It.Is<IReadOnlyCollection<ProcessingEvent>>(events =>
                events.Single().EventType == "upload-item.completed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Detail_AuthorizesBeforeReconciliation()
    {
        var session = new UploadSession
        {
            Id = "session-1",
            JobId = "job-1",
            OwnerActorId = "owner-1",
        };
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(repo => repo.GetOwnedAsync(
                "session-1", "owner-1", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repo => repo.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job
            {
                Id = "job-1",
                OrganizationId = Guid.NewGuid().ToString(),
                DepartmentId = Guid.NewGuid().ToString(),
            });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns("owner-1");
        currentUser.Setup(service => service.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentAuthorizationState(
                new CurrentEntraClaims(
                    "tenant", "owner-1", "owner", "Owner", "owner@example.com",
                    DateTimeOffset.UtcNow, new HashSet<string>(), new HashSet<string>()),
                null, [], [], [], [], "tenant", null));
        var service = new UploadItemLifecycleService(
            sessions.Object,
            currentUser.Object,
            jobs: jobs.Object);

        var act = () => service.GetOwnedAsync("session-1", CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        sessions.Verify(repo => repo.ReconcileStaleAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Detail_ReplaysSucceededApplicationStillAwaitingPublication()
    {
        var item = new UploadItem
        {
            Id = "item-1",
            SessionId = "session-1",
            OccurrenceKey = Guid.NewGuid().ToString(),
            Status = UploadItemStatus.Succeeded,
        };
        item.LinkApplication("app-1");
        var session = new UploadSession
        {
            Id = "session-1",
            JobId = "job-1",
            OwnerActorId = "owner-1",
            Items = [item],
        };
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(repo => repo.GetOwnedAsync(
                "session-1", "owner-1", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(repo => repo.ReconcileStaleAsync(
                "session-1", "owner-1", It.IsAny<DateTime>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(repo => repo.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application
            {
                Id = "app-1",
                JobId = "job-1",
                Status = "Uploading",
            });
        var signal = new Mock<IScoringQueueSignal>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns("owner-1");
        var service = new UploadItemLifecycleService(
            sessions.Object,
            currentUser.Object,
            applications: applications.Object,
            queueSignal: signal.Object);

        var result = await service.GetOwnedAsync("session-1", CancellationToken.None);

        result.Should().NotBeNull();
        applications.Verify(repo => repo.PublishUploadedAsync(
            It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "app-1" })),
            It.IsAny<CancellationToken>()), Times.Once);
        signal.Verify(x => x.Pulse(), Times.Once);
    }
}
