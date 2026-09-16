using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.ExtractionInstructions.Commands;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Application.JobExtraction.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class ExtractionInstructionCommandTests
{
    private readonly Mock<IExtractionInstructionRepository> _repository = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IProcessingEventRepository> _events = new();

    [Fact]
    public async Task CreateDraft_AssignsNextVersion_AndAudits()
    {
        ExtractionInstructionVersion? saved = null;
        _repository.Setup(repository => repository.GetNextVersionNumberAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        _repository.Setup(repository => repository.AddAsync(It.IsAny<ExtractionInstructionVersion>(), It.IsAny<CancellationToken>()))
            .Callback<ExtractionInstructionVersion, CancellationToken>((version, _) => saved = version)
            .Returns(Task.CompletedTask);
        _currentUser.SetupGet(service => service.UserId).Returns("admin-1");
        _currentUser.SetupGet(service => service.IsAdmin).Returns(true);

        var handler = new CreateExtractionInstructionCommandHandler(_repository.Object, _currentUser.Object, _events.Object);

        var result = await handler.Handle(new CreateExtractionInstructionCommand("Extract each requirement individually.", "Improve splitting"), CancellationToken.None);

        result.VersionNumber.Should().Be(2);
        result.Status.Should().Be("draft");
        result.ValidationStatus.Should().Be("unvalidated");
        saved.Should().NotBeNull();
        saved!.CreatedBy.Should().Be("admin-1");
        _events.Verify(repository => repository.AddAsync(It.Is<ProcessingEvent>(evt => evt.EventType == "extraction-instruction.created"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Activate_RejectsStaleConcurrencyVersion()
    {
        _repository.Setup(repository => repository.GetByIdAsync("instruction-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionInstructionVersion
            {
                Id = "instruction-1",
                VersionNumber = 1,
                InstructionText = "Current",
                ProtectedContractVersion = "extraction-rubric-v1",
                Status = "retired",
                ValidationStatus = "valid",
                CreatedBy = "seed",
                ConcurrencyVersion = 3
            });
        _currentUser.SetupGet(service => service.IsAdmin).Returns(true);

        var handler = new ActivateExtractionInstructionCommandHandler(_repository.Object, _currentUser.Object, _events.Object);

        var act = () => handler.Handle(new ActivateExtractionInstructionCommand("instruction-1", 2), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stale_version*");
    }

    [Fact]
    public async Task Validate_UpdatesVersionStatus_FromExtractionResult()
    {
        var version = new ExtractionInstructionVersion
        {
            Id = "instruction-1",
            VersionNumber = 1,
            InstructionText = "Draft",
            ProtectedContractVersion = "extraction-rubric-v1",
            Status = "draft",
            ValidationStatus = "unvalidated",
            CreatedBy = "admin-1",
            ConcurrencyVersion = 1
        };
        _repository.Setup(repository => repository.GetByIdAsync(version.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(version);
        _repository.Setup(repository => repository.UpdateAsync(version, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _currentUser.SetupGet(service => service.IsAdmin).Returns(true);
        _currentUser.SetupGet(service => service.UserId).Returns("admin-1");

        var orchestrator = new Mock<IExtractionInstructionValidationRunner>();
        orchestrator.Setup(runner => runner.ValidateAsync(version.Id, It.IsAny<ExtractDocumentRequestModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobSpecExtractionExecutionResult(
                "extract-1",
                version.Id,
                version.ProtectedContractVersion,
                "valid",
                [],
                "Title",
                "Description",
                "Department",
                "Org",
                null));

        var handler = new ValidateExtractionInstructionCommandHandler(_repository.Object, orchestrator.Object, _currentUser.Object, _events.Object);

        var result = await handler.Handle(new ValidateExtractionInstructionCommand(version.Id, "sample.md", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sample")), "text/markdown"), CancellationToken.None);

        result.ValidationStatus.Should().Be("valid");
        version.ValidationStatus.Should().Be("valid");
        version.ValidatedBy.Should().Be("admin-1");
    }
}
