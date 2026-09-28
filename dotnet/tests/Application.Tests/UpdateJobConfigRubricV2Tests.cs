using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class UpdateJobConfigRubricV2Tests
{
    private readonly Mock<IJobRepository> _jobRepository = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IJobSpecExtractionRepository> _extractionRepository = new();

    [Fact]
    public async Task Handle_WhenExtractionIsAlreadyLinked_PersistsProvenanceWithoutRelinking()
    {
        var job = BuildJob("cfg-001");
        var versions = new List<JobConfigVersion>
        {
            new() { Id = "cfg-001", JobId = job.Id, VersionNumber = 1, RubricJson = "[]" }
        };
        _jobRepository.Setup(repository => repository.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _jobRepository.Setup(repository => repository.GetConfigVersionsAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions);
        _jobRepository.Setup(repository => repository.AddConfigVersionAsync(It.IsAny<JobConfigVersion>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jobRepository.Setup(repository => repository.UpdateAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _extractionRepository.Setup(repository => repository.GetByIdAsync("extract-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobSpecExtraction
            {
                Id = "extract-001",
                JobId = job.Id,
                JobConfigVersionId = "cfg-001"
            });
        ConfigureCurrentUser();

        var handler = new UpdateJobConfigCommandHandler(
            _jobRepository.Object,
            _currentUser.Object,
            null,
            _extractionRepository.Object);

        var result = await handler.Handle(new UpdateJobConfigCommand(
            job.Id,
            """{"schemaVersion":"rubric-v2","categories":[{"id":"cat-1","name":"Technical Skills","weight":1,"description":"desc","order":0}],"items":[{"id":"item-1","categoryId":"cat-1","text":"Expert SQL experience","requirementType":"must_have","order":0,"sourceText":"Expert SQL experience","sourceLocation":null,"sourceRequirementId":"req-1","reviewStatus":"confirmed","createdFrom":"extracted"}]}""",
            """[{"criterion":"Expert SQL experience","description":"Expert SQL experience"}]""",
            "[]",
            3,
            "median",
            70,
            85,
            15,
            "extracted",
            "{}",
            "extract-001",
            "instruction-v1",
            "cfg-001",
            "approved"), CancellationToken.None);

        result.VersionNumber.Should().Be(2);
        result.ExtractionId.Should().Be("extract-001");
        result.ExtractionInstructionVersionId.Should().Be("instruction-v1");
        job.CurrentConfigVersionId.Should().Be(result.Id);
        _extractionRepository.Verify(repository => repository.LinkToJobConfigAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenExtractionIsNotLinked_LinksItToCreatedVersion()
    {
        var job = BuildJob("cfg-001");
        _jobRepository.Setup(repository => repository.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _jobRepository.Setup(repository => repository.GetConfigVersionsAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new JobConfigVersion { Id = "cfg-001", JobId = job.Id, VersionNumber = 1 }]);
        _jobRepository.Setup(repository => repository.AddConfigVersionAsync(
                It.IsAny<JobConfigVersion>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jobRepository.Setup(repository => repository.UpdateAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _extractionRepository.Setup(repository => repository.GetByIdAsync("extract-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobSpecExtraction { Id = "extract-001" });
        ConfigureCurrentUser();

        var handler = new UpdateJobConfigCommandHandler(
            _jobRepository.Object,
            _currentUser.Object,
            null,
            _extractionRepository.Object);

        var result = await handler.Handle(BuildCommand(job.Id, "extract-001"), CancellationToken.None);

        _extractionRepository.Verify(repository => repository.LinkToJobConfigAsync(
            "extract-001", job.Id, result.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenExtractionBelongsToAnotherJob_FailsBeforePersisting()
    {
        var job = BuildJob("cfg-001");
        _jobRepository.Setup(repository => repository.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _extractionRepository.Setup(repository => repository.GetByIdAsync("extract-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobSpecExtraction
            {
                Id = "extract-001",
                JobId = "job-2",
                JobConfigVersionId = "cfg-other"
            });
        ConfigureCurrentUser();

        var handler = new UpdateJobConfigCommandHandler(
            _jobRepository.Object,
            _currentUser.Object,
            null,
            _extractionRepository.Object);

        var act = () => handler.Handle(BuildCommand(job.Id, "extract-001"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidJobConfigException>()
            .WithMessage("*different job*");
        _jobRepository.Verify(repository => repository.AddConfigVersionAsync(
            It.IsAny<JobConfigVersion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenExpectedConfigVersionIsStale_Throws()
    {
        var job = BuildJob("cfg-002");
        _jobRepository.Setup(repository => repository.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _jobRepository.Setup(repository => repository.GetConfigVersionsAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new JobConfigVersion { Id = "cfg-002", JobId = job.Id, VersionNumber = 2 }]);
        ConfigureCurrentUser();

        var handler = new UpdateJobConfigCommandHandler(
            _jobRepository.Object,
            _currentUser.Object,
            null,
            _extractionRepository.Object);

        var act = () => handler.Handle(new UpdateJobConfigCommand(
            job.Id,
            """{"schemaVersion":"rubric-v2","categories":[{"id":"cat-1","name":"Technical Skills","weight":1,"description":"desc","order":0}],"items":[]}""",
            "[]",
            "[]",
            3,
            "median",
            70,
            85,
            15,
            "manual",
            null,
            null,
            null,
            "cfg-001",
            "draft"), CancellationToken.None);

        await act.Should().ThrowAsync<JobConfigVersionConflictException>()
            .WithMessage("*stale_version*");
    }

    private static UpdateJobConfigCommand BuildCommand(string jobId, string? extractionId)
        => new(
            jobId,
            """{"schemaVersion":"rubric-v2","categories":[{"id":"cat-1","name":"Technical Skills","weight":1,"description":"desc","order":0}],"items":[]}""",
            "[]",
            "[]",
            3,
            "median",
            70,
            85,
            15,
            extractionId is null ? "manual" : "extracted",
            extractionId is null ? null : "{}",
            extractionId,
            extractionId is null ? null : "instruction-v1",
            "cfg-001",
            "draft");

    private void ConfigureCurrentUser()
    {
        _currentUser.SetupGet(service => service.UserId).Returns("user-1");
        _currentUser.SetupGet(service => service.Username).Returns("user@example.com");
        _currentUser.Setup(service => service.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CurrentAuthorizationState?)null);
    }

    private static Job BuildJob(string currentConfigVersionId)
        => new()
        {
            Id = "job-1",
            Title = "Senior Data Platform Engineer",
            Department = "Data Engineering",
            Organisation = "Northwind Analytics",
            Status = "active",
            CurrentConfigVersionId = currentConfigVersionId,
        };
}
