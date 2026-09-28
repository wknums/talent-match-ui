using FluentAssertions;
using Moq;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class UploadApplicationsCommandTests
{
    [Fact]
    public async Task Handle_PublishesOnlyAfterAllDocumentsHaveBeenSaved()
    {
        var applications = new Mock<IApplicationRepository>();
        var signal = new Mock<IScoringQueueSignal>();
        var savedDocuments = 0;
        applications.Setup(repo => repo.AddAsync(It.IsAny<Domain.Entities.Application>(), It.IsAny<CancellationToken>()))
            .Callback<Domain.Entities.Application, CancellationToken>((application, _) =>
                application.Status.Should().Be("Uploading"));
        applications.Setup(repo => repo.AddDocumentAsync(It.IsAny<ApplicationDocument>(), It.IsAny<CancellationToken>()))
            .Callback(() => savedDocuments++);
        applications.Setup(repo => repo.PublishUploadedAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>, CancellationToken>((ids, _) =>
            {
                ids.Should().HaveCount(2);
                savedDocuments.Should().Be(2);
                signal.Verify(x => x.Pulse(), Times.Never);
            });
        var handler = new UploadApplicationsCommandHandler(applications.Object, CreateJobRepository().Object, signal.Object);

        var result = await handler.Handle(new UploadApplicationsCommand(
            "job-1", [CreateFile(), CreateFile()], AllowDuplicates: true), CancellationToken.None);

        result.Should().HaveCount(2).And.OnlyContain(application => application.Status == "Queued");
        applications.Verify(repo => repo.PublishUploadedAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        signal.Verify(x => x.Pulse(), Times.Once);
    }

    [Fact]
    public async Task Handle_DoesNotPublishAPartialUpload_WhenDocumentPersistenceFails()
    {
        var applications = new Mock<IApplicationRepository>();
        var signal = new Mock<IScoringQueueSignal>();
        applications.SetupSequence(repo => repo.AddDocumentAsync(It.IsAny<ApplicationDocument>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .ThrowsAsync(new InvalidOperationException("Storage unavailable"));
        var handler = new UploadApplicationsCommandHandler(applications.Object, CreateJobRepository().Object, signal.Object);

        var act = () => handler.Handle(new UploadApplicationsCommand(
            "job-1", [CreateFile(), CreateFile()], AllowDuplicates: true), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        applications.Verify(repo => repo.PublishUploadedAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        signal.Verify(x => x.Pulse(), Times.Never);
    }

    [Fact]
    public async Task Handle_SkipsExistingFingerprintByDefault()
    {
        var applications = new Mock<IApplicationRepository>();
        var jobs = CreateJobRepository();
        applications.Setup(x => x.FindDocumentByFingerprintAsync(
                "job-1", "fingerprint-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApplicationDocument
            {
                ApplicationId = "existing-app",
                FileName = "existing.pdf",
                Fingerprint = "fingerprint-1",
            });
        var handler = new UploadApplicationsCommandHandler(applications.Object, jobs.Object);

        var result = await handler.Handle(
            new UploadApplicationsCommand("job-1", [CreateFile()], AllowDuplicates: false),
            CancellationToken.None);

        result.Should().BeEmpty();
        applications.Verify(
            x => x.AddAsync(It.IsAny<Domain.Entities.Application>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_CreatesApplicationForExistingFingerprintWhenDuplicatesAllowed()
    {
        var applications = new Mock<IApplicationRepository>();
        var jobs = CreateJobRepository();
        var handler = new UploadApplicationsCommandHandler(applications.Object, jobs.Object);

        var result = await handler.Handle(
            new UploadApplicationsCommand("job-1", [CreateFile()], AllowDuplicates: true),
            CancellationToken.None);

        result.Should().ContainSingle();
        applications.Verify(
            x => x.FindDocumentByFingerprintAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        applications.Verify(
            x => x.AddDocumentAsync(
                It.Is<ApplicationDocument>(document => document.Fingerprint == "fingerprint-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_LegacyPathStillSkipsLaterMatchingOccurrenceAndPublishesOnce()
    {
        var applications = new Mock<IApplicationRepository>();
        var signal = new Mock<IScoringQueueSignal>();
        var handler = new UploadApplicationsCommandHandler(
            applications.Object, CreateJobRepository().Object, signal.Object);

        var result = await handler.Handle(new UploadApplicationsCommand(
            "job-1", [CreateFile(), CreateFile()], AllowDuplicates: false), CancellationToken.None);

        result.Should().ContainSingle().Which.Status.Should().Be("Queued");
        applications.Verify(x => x.PublishUploadedAsync(
            It.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        signal.Verify(x => x.Pulse(), Times.Once);
    }

    private static Mock<IJobRepository> CreateJobRepository()
    {
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(x => x.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", Title = "Test Job" });
        return jobs;
    }

    private static UploadedFile CreateFile()
        => new(
            "candidate.pdf",
            "application/pdf",
            3,
            Convert.ToBase64String("cv"u8.ToArray()),
            "fingerprint-1");
}
