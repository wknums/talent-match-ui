using FluentAssertions;
using Moq;
using TalentMatch.Application.Uploads.Commands;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Prompts.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class OptionalUploadSessionTests
{
    [Theory]
    [InlineData("a.pdf", "", "application/pdf")]
    [InlineData("a.md", "", "text/markdown")]
    [InlineData("a.docx", "", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("a.txt", "", "text/plain")]
    [InlineData("a.jpg", "", "image/jpeg")]
    [InlineData("a.png", "", "image/png")]
    public void MimeNormalization_AllowsExactlyTheOptionalContractTypes(
        string fileName, string supplied, string expected) =>
        UploadValidators.NormalizeMimeType(fileName, supplied).Should().Be(expected);

    [Fact]
    public void Eligibility_AcceptsExactLimitAndRejectsOneByteOverPerItem()
    {
        UploadValidators.ValidateItem(Item(4_194_304), 4_194_304).Should().BeEmpty();
        var errors = UploadValidators.ValidateItem(Item(4_194_305), 4_194_304);
        errors.Should().ContainSingle().Which.Should().Contain("4194304");
    }

    [Fact]
    public async Task Create_CommitsSessionAndEveryOccurrenceWithDefaultSnapshot()
    {
        var settings = new Mock<IUploadSettingsRepository>();
        settings.Setup(x => x.GetPersistedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSettings?)null);
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(x => x.CreateAsync(
                It.IsAny<UploadSession>(), It.IsAny<IReadOnlyCollection<UploadItem>>(),
                It.IsAny<ProcessingEvent>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSession session, IReadOnlyCollection<UploadItem> items, ProcessingEvent _, int _, CancellationToken _) =>
            {
                session.Items = items.ToList();
                return session;
            });
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(x => x.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", Title = "Job" });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        currentUser.SetupGet(x => x.Username).Returns("Owner");
        var handler = new CreateUploadSessionCommandHandler(
            settings.Object, sessions.Object, jobs.Object, currentUser.Object);
        var occurrences = new[] { Item(10, 0), Item(20, 1) };

        var result = await handler.Handle(
            new CreateUploadSessionCommand("job-1", new(false, occurrences), "correlation-1"),
            CancellationToken.None);

        result.Limits.FileConcurrency.Should().Be(4);
        result.Limits.MaxIndividualFileBytes.Should().Be(4_194_304);
        result.Limits.MaxInFlightBytes.Should().Be(104_857_600);
        result.Items.Select(x => x.OccurrenceKey).Should()
            .Equal(occurrences.Select(x => x.OccurrenceKey));
        sessions.Verify(x => x.CreateAsync(
            It.IsAny<UploadSession>(),
            It.Is<IReadOnlyCollection<UploadItem>>(items => items.Count == 2),
            It.IsAny<ProcessingEvent>(),
            0,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_RetriesValidationWhenSettingsChangeBeforeCommit()
    {
        var latest = new UploadSettings
        {
            FileConcurrency = 2,
            MaxIndividualFileBytes = 20_000_000,
            MaxInFlightBytes = 40_000_000,
            ConcurrencyVersion = 1,
        };
        var settings = new Mock<IUploadSettingsRepository>();
        settings.SetupSequence(x => x.GetPersistedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSettings?)null)
            .ReturnsAsync(latest);
        var versions = new List<int>();
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(x => x.CreateAsync(
                It.IsAny<UploadSession>(),
                It.IsAny<IReadOnlyCollection<UploadItem>>(),
                It.IsAny<ProcessingEvent>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                UploadSession session,
                IReadOnlyCollection<UploadItem> items,
                ProcessingEvent _,
                int expectedSettingsVersion,
                CancellationToken _) =>
            {
                versions.Add(expectedSettingsVersion);
                if (expectedSettingsVersion == 0)
                    throw new InvalidOperationException("settings_stale");
                session.Items = items.ToList();
                return session;
            });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var handler = new CreateUploadSessionCommandHandler(
            settings.Object,
            sessions.Object,
            CreateJobRepository().Object,
            currentUser.Object);

        var result = await handler.Handle(
            new CreateUploadSessionCommand(
                "job-1",
                new(false, [Item(1)]),
                "correlation-1"),
            CancellationToken.None);

        versions.Should().Equal(0, 1);
        result.Limits.FileConcurrency.Should().Be(2);
        result.Limits.MaxIndividualFileBytes.Should().Be(20_000_000);
        result.Limits.MaxInFlightBytes.Should().Be(40_000_000);
    }

    [Fact]
    public async Task Create_WithNoEligibleOccurrence_DoesNotCreateSession()
    {
        var settings = new Mock<IUploadSettingsRepository>();
        settings.Setup(x => x.GetPersistedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSettings?)null);
        var sessions = new Mock<IUploadSessionRepository>();
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(x => x.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", Title = "Job" });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var handler = new CreateUploadSessionCommandHandler(
            settings.Object, sessions.Object, jobs.Object, currentUser.Object);

        var act = () => handler.Handle(new CreateUploadSessionCommand(
            "job-1", new(false, [Item(4_194_305)]), "correlation-1"), CancellationToken.None);

        await act.Should().ThrowAsync<UploadValidationException>();
        sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Content_TerminalReplayReturnsRecordedOutcomeWithoutCreatingApplication()
    {
        var sessions = new Mock<IUploadSessionRepository>();
        var terminal = new UploadItem
        {
            Id = "item-1",
            SessionId = "session-1",
            OccurrenceKey = Guid.NewGuid().ToString(),
            Status = UploadItemStatus.Succeeded,
        };
        terminal.LinkApplication("app-1");
        sessions.Setup(x => x.GetItemAsync(
                "session-1", "item-1", "owner-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(terminal);
        sessions.Setup(x => x.GetOwnedAsync(
                "session-1", "owner-1", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadSession
            {
                Id = "session-1",
                JobId = "job-1",
                OwnerActorId = "owner-1",
            });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(x => x.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application
            {
                Id = "app-1",
                JobId = "job-1",
                Status = "Uploading",
            });
        var handler = new UploadItemContentCommandHandler(
            sessions.Object, applications.Object, currentUser.Object);

        var result = await handler.Handle(new UploadItemContentCommand(
            "session-1", "item-1", Guid.Parse(terminal.OccurrenceKey),
            "candidate.pdf", "application/pdf", "cv"u8.ToArray(), "correlation-1"),
            CancellationToken.None);

        result.ApplicationId.Should().Be("app-1");
        sessions.Verify(x => x.CompleteItemAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        applications.Verify(x => x.PublishUploadedAsync(
            It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "app-1" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Content_SuccessAtAtomicQueuedBoundaryPulsesScoring()
    {
        var occurrence = Guid.NewGuid();
        var item = new UploadItem
        {
            Id = "item-1",
            SessionId = "session-1",
            OccurrenceKey = occurrence.ToString(),
            FileName = "candidate.pdf",
            MimeType = "application/pdf",
            RawSizeBytes = 2,
        };
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(x => x.GetItemAsync(
                "session-1", "item-1", "owner-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        sessions.Setup(x => x.GetOwnedAsync(
                "session-1", "owner-1", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadSession
            {
                Id = "session-1",
                OwnerActorId = "owner-1",
                MaxIndividualFileBytes = 4_194_304,
                MaxInFlightBytes = 104_857_600,
                FileConcurrency = 4,
            });
        sessions.Setup(x => x.UpdateItemAsync(
                It.IsAny<UploadItem>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyCollection<ProcessingEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadItem value, int _, IReadOnlyCollection<ProcessingEvent> _, CancellationToken _) => value);
        sessions.Setup(x => x.CompleteItemAsync(
                "session-1", "item-1", "owner-1", occurrence.ToString(),
                It.IsAny<string>(), "candidate.pdf", "application/pdf", 2,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                item.LinkApplication("app-1");
                item.Status = UploadItemStatus.Succeeded;
                return item;
            });
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(x => x.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application
            {
                Id = "app-1",
                JobId = "job-1",
                Status = "Queued",
            });
        var signal = new Mock<IScoringQueueSignal>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var handler = new UploadItemContentCommandHandler(
            sessions.Object, applications.Object, currentUser.Object, signal.Object);

        var result = await handler.Handle(new UploadItemContentCommand(
            "session-1", "item-1", occurrence, "candidate.pdf",
            "application/pdf", "cv"u8.ToArray(), "correlation-1"), CancellationToken.None);

        result.Status.Should().Be("succeeded");
        applications.Verify(x => x.PublishUploadedAsync(
            It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        signal.Verify(x => x.Pulse(), Times.Once);
    }

    [Fact]
    public async Task Content_TerminalReplayRecoversPublicationFailure()
    {
        var occurrence = Guid.NewGuid();
        var item = new UploadItem
        {
            Id = "item-1",
            SessionId = "session-1",
            OccurrenceKey = occurrence.ToString(),
            Status = UploadItemStatus.Succeeded,
        };
        item.LinkApplication("app-1");
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(x => x.GetOwnedAsync(
                "session-1", "owner-1", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadSession
            {
                Id = "session-1",
                JobId = "job-1",
                OwnerActorId = "owner-1",
            });
        sessions.Setup(x => x.GetItemAsync(
                "session-1", "item-1", "owner-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(x => x.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application
            {
                Id = "app-1",
                JobId = "job-1",
                Status = "Uploading",
            });
        applications.SetupSequence(x => x.PublishUploadedAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("publication unavailable"))
            .Returns(Task.CompletedTask);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var handler = new UploadItemContentCommandHandler(
            sessions.Object, applications.Object, currentUser.Object);
        var command = new UploadItemContentCommand(
            "session-1", "item-1", occurrence, "candidate.pdf",
            "application/pdf", "cv"u8.ToArray(), "correlation-1");

        await FluentActions.Invoking(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        var replay = await handler.Handle(command, CancellationToken.None);

        replay.Status.Should().Be("succeeded");
        applications.Verify(x => x.PublishUploadedAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Create_RequiresTheLegacyProductionPromptProfileGuard()
    {
        var settings = new Mock<IUploadSettingsRepository>();
        var sessions = new Mock<IUploadSessionRepository>();
        var jobs = CreateJobRepository();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var prompts = new Mock<IScoringPromptRepository>();
        var profileGuard = new Mock<IPromptProfileGuard>();
        var handler = new CreateUploadSessionCommandHandler(
            settings.Object,
            sessions.Object,
            jobs.Object,
            currentUser.Object,
            organizations: null,
            prompts: prompts.Object,
            profileGuard: profileGuard.Object);

        var act = () => handler.Handle(
            new CreateUploadSessionCommand(
                "job-1", new(false, [Item(1)]), "correlation-1"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A production-approved prompt is required before applications can be queued.");
        sessions.VerifyNoOtherCalls();
        profileGuard.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Create_BoundsInvalidDisplayNameWithoutRollingBackValidItems()
    {
        var settings = new Mock<IUploadSettingsRepository>();
        settings.Setup(x => x.GetPersistedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSettings?)null);
        IReadOnlyCollection<UploadItem>? persistedItems = null;
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(x => x.CreateAsync(
                It.IsAny<UploadSession>(), It.IsAny<IReadOnlyCollection<UploadItem>>(),
                It.IsAny<ProcessingEvent>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSession session, IReadOnlyCollection<UploadItem> items, ProcessingEvent _, int _, CancellationToken _) =>
            {
                persistedItems = items;
                session.Items = items.ToList();
                return session;
            });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        var handler = new CreateUploadSessionCommandHandler(
            settings.Object, sessions.Object, CreateJobRepository().Object, currentUser.Object);
        var tooLong = new string('x', 501) + ".pdf";

        var result = await handler.Handle(
            new CreateUploadSessionCommand(
                "job-1",
                new(false,
                [
                    new(Guid.NewGuid(), 0, tooLong, "application/pdf", 1),
                    Item(1, 1),
                ]),
                "correlation-1"),
            CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items[0].Status.Should().Be("failed");
        persistedItems.Should().NotBeNull();
        var capturedItems = persistedItems!;
        capturedItems.Single(item => item.Ordinal == 0).FileName.Should().HaveLength(500);
        capturedItems.Single(item => item.Ordinal == 1).Status.Should().Be(UploadItemStatus.Waiting);
    }

    [Fact]
    public async Task Content_ReauthorizesJobBeforeReadingOrMutatingTheItem()
    {
        var organizationId = Guid.NewGuid().ToString();
        var departmentId = Guid.NewGuid().ToString();
        var sessions = new Mock<IUploadSessionRepository>();
        sessions.Setup(x => x.GetOwnedAsync(
                "session-1", "owner-1", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadSession
            {
                Id = "session-1",
                JobId = "job-1",
                OwnerActorId = "owner-1",
            });
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(x => x.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job
            {
                Id = "job-1",
                OrganizationId = organizationId,
                DepartmentId = departmentId,
            });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("owner-1");
        currentUser.Setup(x => x.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentAuthorizationState(
                new CurrentEntraClaims(
                    "tenant", "owner-1", "owner", "Owner", "owner@example.com",
                    DateTimeOffset.UtcNow, new HashSet<string>(), new HashSet<string>()),
                null, [], [], [], [], "tenant", null));
        var handler = new UploadItemContentCommandHandler(
            sessions.Object,
            Mock.Of<IApplicationRepository>(),
            currentUser.Object,
            jobRepository: jobs.Object);

        var act = () => handler.Handle(
            new UploadItemContentCommand(
                "session-1",
                "item-1",
                Guid.NewGuid(),
                "candidate.pdf",
                "application/pdf",
                "cv"u8.ToArray(),
                "correlation-1"),
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        sessions.Verify(x => x.GetItemAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CreateUploadItemRequest Item(long size, int ordinal = 0) =>
        new(Guid.NewGuid(), ordinal, $"candidate-{ordinal}.pdf", "application/pdf", size);

    private static Mock<IJobRepository> CreateJobRepository()
    {
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(x => x.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", Title = "Job" });
        return jobs;
    }
}
