using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Prompts.Commands;
using TalentMatch.Application.Prompts.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class PromptProfileLifecycleTests
{
    private static readonly ScoringProfile CurrentProfile = new("model-current", "high");

    [Fact]
    public async Task Edit_creates_new_version_without_mutating_source()
    {
        var source = new ScoringPrompt
        {
            Id = "prompt-1",
            JobId = "job-1",
            VersionNumber = 2,
            PromptText = "original",
            Status = "production-approved",
            Source = "generated",
            ModelId = "old-model",
            ReasoningLevel = "low",
        };
        var repository = new Mock<IScoringPromptRepository>();
        repository.Setup(item => item.GetByIdAsync("prompt-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        repository.Setup(item => item.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([source]);
        ScoringPrompt? added = null;
        repository.Setup(item => item.AddAsync(It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringPrompt, CancellationToken>((prompt, _) => added = prompt)
            .Returns(Task.CompletedTask);
        var profiles = ProfileProvider(CurrentProfile);

        var result = await new EditPromptCommandHandler(repository.Object, profiles.Object)
            .Handle(new EditPromptCommand("prompt-1", "edited", "reviewer"), CancellationToken.None);

        source.PromptText.Should().Be("original");
        source.Status.Should().Be("production-approved");
        result.Should().BeSameAs(added);
        result.VersionNumber.Should().Be(3);
        result.Status.Should().Be("draft");
        result.PromptText.Should().Be("edited");
        result.ModelId.Should().Be(CurrentProfile.ModelId);
        result.ReasoningLevel.Should().Be(CurrentProfile.ReasoningLevel);
        repository.Verify(item => item.UpdateAsync(
            It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Generate_prefers_job_instruction_and_snapshots_provenance_and_profile()
    {
        var prompts = new Mock<IScoringPromptRepository>();
        prompts.Setup(item => item.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        ScoringPrompt? added = null;
        prompts.Setup(item => item.AddAsync(It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringPrompt, CancellationToken>((prompt, _) => added = prompt)
            .Returns(Task.CompletedTask);
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(item => item.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JobWithApprovedRubric());
        var instructions = new Mock<IPromptGenerationInstructionRepository>();
        instructions.Setup(item => item.GetActiveAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptGenerationInstruction
            {
                Id = "job-instruction",
                JobId = "job-1",
                InstructionText = "job-specific instruction",
                ModelId = "passthrough-llm",
                ReasoningLevel = "medium",
                Status = "active",
            });
        var llm = new Mock<ILlmProxyService>();
        llm.Setup(item => item.SendPromptAsync(
                "job-specific instruction",
                It.IsAny<string>(),
                CurrentProfile,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("generated prompt");
        var reasoningModels = new Mock<IReasoningModelCatalog>();
        reasoningModels.Setup(item => item.ResolveForExecutionAsync(
                "passthrough-llm",
                "medium",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CurrentProfile);
        reasoningModels.Setup(item => item.ValidateAsync(
                "o3",
                "high",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CurrentProfile);

        await new GeneratePromptCommandHandler(
                prompts.Object, jobs.Object, llm.Object, instructions.Object,
                ProfileProvider(CurrentProfile).Object,
                reasoningModels.Object)
            .Handle(new GeneratePromptCommand("job-1", "author"), CancellationToken.None);

        added.Should().NotBeNull();
        added!.GenerationInstructionVersionId.Should().Be("job-instruction");
        added.ModelId.Should().Be(CurrentProfile.ModelId);
        added.ReasoningLevel.Should().Be(CurrentProfile.ReasoningLevel);
        added.GenerationMetadataJson.Should().Contain("\"GenerationInstructionScope\":\"job\"");
        added.PromptText.Should().Contain("## Output Contract (Mandatory)");
        reasoningModels.Verify(item => item.ResolveForExecutionAsync(
            "passthrough-llm",
            "medium",
            It.IsAny<CancellationToken>()), Times.Once);
        instructions.Verify(item => item.GetActiveAsync(
            null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Generate_falls_back_to_active_system_instruction()
    {
        var prompts = new Mock<IScoringPromptRepository>();
        prompts.Setup(item => item.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        ScoringPrompt? added = null;
        prompts.Setup(item => item.AddAsync(It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringPrompt, CancellationToken>((prompt, _) => added = prompt)
            .Returns(Task.CompletedTask);
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(item => item.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JobWithApprovedRubric());
        var instructions = new Mock<IPromptGenerationInstructionRepository>();
        instructions.Setup(item => item.GetActiveAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptGenerationInstruction?)null);
        instructions.Setup(item => item.GetActiveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptGenerationInstruction
            {
                Id = "system-instruction",
                InstructionText = "system instruction",
                ModelId = CurrentProfile.ModelId,
                ReasoningLevel = CurrentProfile.ReasoningLevel,
                Status = "active",
            });
        var llm = new Mock<ILlmProxyService>();
        llm.Setup(item => item.SendPromptAsync(
                "system instruction", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("generated");

        await new GeneratePromptCommandHandler(
                prompts.Object, jobs.Object, llm.Object, instructions.Object,
                ProfileProvider(CurrentProfile).Object)
            .Handle(new GeneratePromptCommand("job-1", "author"), CancellationToken.None);

        added!.GenerationInstructionVersionId.Should().Be("system-instruction");
        added.GenerationMetadataJson.Should().Contain("\"GenerationInstructionScope\":\"system\"");
    }

    [Fact]
    public async Task Profile_guard_requires_test_evidence_for_the_prompt_profile()
    {
        var prompt = new ScoringPrompt
        {
            Id = "prompt-1",
            VersionNumber = 4,
            ModelId = "model-old",
            ReasoningLevel = "high",
            ApprovedModelId = "model-old",
            ApprovedReasoningLevel = "high",
        };
        var tests = new Mock<IPromptTestRunRepository>();
        tests.Setup(item => item.GetByPromptIdAsync("prompt-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PromptTestRun
                {
                    Id = "test-old",
                    PromptId = "prompt-1",
                    Status = "approved",
                    ModelId = "model-old",
                    ReasoningLevel = "low",
                    ApprovedModelId = "model-old",
                    ApprovedReasoningLevel = "high",
                }
            ]);
        var guard = new PromptProfileGuard(ProfileProvider(CurrentProfile).Object, tests.Object);

        var status = await guard.GetStatusAsync(prompt);

        status.IsMatch.Should().BeFalse();
        status.HasExactProfileApprovedTest.Should().BeFalse();
        status.MismatchMessage.Should().Contain("Create a new prompt version");
        var action = () => guard.EnsureProductionReadyAsync(prompt);
        await action.Should().ThrowAsync<ScoringProfileMismatchException>();
    }

    [Fact]
    public async Task Production_approval_snapshots_exact_approved_test_profile()
    {
        var prompt = new ScoringPrompt
        {
            Id = "prompt-1",
            JobId = "job-1",
            VersionNumber = 5,
            ModelId = CurrentProfile.ModelId,
            ReasoningLevel = CurrentProfile.ReasoningLevel,
        };
        var prompts = new Mock<IScoringPromptRepository>();
        prompts.Setup(item => item.GetByIdAsync("prompt-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(prompt);
        prompts.Setup(item => item.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([prompt]);
        var tests = new Mock<IPromptTestRunRepository>();
        tests.Setup(item => item.GetByPromptIdAsync("prompt-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PromptTestRun
                {
                    Id = "test-current",
                    PromptId = "prompt-1",
                    Status = "approved",
                    ModelId = CurrentProfile.ModelId,
                    ReasoningLevel = CurrentProfile.ReasoningLevel,
                    ApprovedModelId = CurrentProfile.ModelId,
                    ApprovedReasoningLevel = CurrentProfile.ReasoningLevel,
                    CompletedAt = DateTime.UtcNow,
                }
            ]);
        var events = new Mock<IProcessingEventRepository>();

        await new ApprovePromptForProductionCommandHandler(
                prompts.Object, tests.Object, events.Object, ProfileProvider(CurrentProfile).Object)
            .Handle(new ApprovePromptForProductionCommand("prompt-1", "reviewer"), CancellationToken.None);

        prompt.Status.Should().Be("production-approved");
        prompt.ApprovedTestRunId.Should().Be("test-current");
        prompt.ApprovedModelId.Should().Be(CurrentProfile.ModelId);
        prompt.ApprovedReasoningLevel.Should().Be(CurrentProfile.ReasoningLevel);
    }

    [Fact]
    public async Task Test_approval_snapshots_the_test_run_profile()
    {
        var testRun = new PromptTestRun
        {
            Id = "test-old",
            PromptId = "prompt-1",
            ModelId = "model-old",
            ReasoningLevel = "high",
        };
        var tests = new Mock<IPromptTestRunRepository>();
        tests.Setup(item => item.GetByIdAsync("test-old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(testRun);
        var handler = new ApprovePromptTestRunCommandHandler(
            tests.Object,
            Mock.Of<IApplicationRepository>(),
            Mock.Of<IProcessingEventRepository>(),
            ProfileProvider(CurrentProfile).Object);

        var result = await handler.Handle(
            new ApprovePromptTestRunCommand("test-old", "reviewer", null),
            CancellationToken.None);

        result.Status.Should().Be("approved");
        result.ApprovedModelId.Should().Be("model-old");
        result.ApprovedReasoningLevel.Should().Be("high");
    }

    [Fact]
    public async Task Production_approval_rejects_prompt_without_matching_test_evidence()
    {
        var prompt = new ScoringPrompt
        {
            Id = "prompt-old",
            JobId = "job-1",
            VersionNumber = 3,
            ModelId = "model-old",
            ReasoningLevel = CurrentProfile.ReasoningLevel,
        };
        var prompts = new Mock<IScoringPromptRepository>();
        prompts.Setup(item => item.GetByIdAsync(prompt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(prompt);
        var testRuns = new Mock<IPromptTestRunRepository>();
        testRuns.Setup(item => item.GetByPromptIdAsync(
                prompt.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var handler = new ApprovePromptForProductionCommandHandler(
            prompts.Object,
            testRuns.Object,
            Mock.Of<IProcessingEventRepository>(),
            ProfileProvider(CurrentProfile).Object);

        var action = () => handler.Handle(
            new ApprovePromptForProductionCommand(prompt.Id, "reviewer"),
            CancellationToken.None);

        await action.Should().ThrowAsync<ScoringProfileMismatchException>()
            .WithMessage("*no approved test run*");
        prompt.Status.Should().NotBe("production-approved");
    }

    private static Mock<IScoringProfileProvider> ProfileProvider(ScoringProfile profile)
    {
        var provider = new Mock<IScoringProfileProvider>();
        provider.SetupGet(item => item.Current).Returns(profile);
        return provider;
    }

    private static Job JobWithApprovedRubric()
        => new()
        {
            Id = "job-1",
            Title = "Engineer",
            Department = "Engineering",
            Organisation = "Example",
            ConfigVersions =
            [
                new JobConfigVersion
                {
                    VersionNumber = 1,
                    RubricApprovalStatus = "approved",
                    RubricJson = """[{"name":"Skills","weight":1,"description":"Match"}]""",
                    MustHavesJson = "[]",
                    DesiredCriteriaJson = "[]",
                }
            ],
        };
}
