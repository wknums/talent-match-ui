using FluentAssertions;
using Moq;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class UploadApplicationsCommandTests
{
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
