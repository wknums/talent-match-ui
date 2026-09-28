using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public sealed class OptionalUploadPersistenceTests
{
    [Fact]
    public async Task UploadForeignKeys_UseSharedIdentifierWidths()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Context.Model;

        model.FindEntityType(typeof(UploadSession))!.FindProperty(nameof(UploadSession.Id))!
            .GetMaxLength().Should().Be(36);
        model.FindEntityType(typeof(UploadSession))!.FindProperty(nameof(UploadSession.JobId))!
            .GetMaxLength().Should().Be(36);
        model.FindEntityType(typeof(UploadItem))!.FindProperty(nameof(UploadItem.Id))!
            .GetMaxLength().Should().Be(36);
        model.FindEntityType(typeof(UploadItem))!.FindProperty(nameof(UploadItem.SessionId))!
            .GetMaxLength().Should().Be(36);
        model.FindEntityType(typeof(UploadItem))!.FindProperty(nameof(UploadItem.ApplicationId))!
            .GetMaxLength().Should().Be(36);
    }

    [Fact]
    public async Task SessionCreation_IsAtomicAndOccurrenceKeysAreUnique()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repo = new UploadSessionRepository(fixture.Context);
        var session = Session();
        var occurrence = Guid.NewGuid().ToString();
        var items = new[]
        {
            Item(session.Id, "item-1", occurrence, 0),
            Item(session.Id, "item-2", occurrence, 1),
        };
        var act = () => repo.CreateAsync(session, items, Event(session), 0);
        await act.Should().ThrowAsync<DbUpdateException>();
        (await fixture.Context.UploadSessions.CountAsync()).Should().Be(0);
        (await fixture.Context.UploadItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task StaleSession_ReconcilesEveryNonterminalItemToInterrupted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repo = new UploadSessionRepository(fixture.Context);
        var session = Session();
        session.LastHeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
        await repo.CreateAsync(
            session,
            [Item(session.Id, "item-1", Guid.NewGuid().ToString(), 0)],
            Event(session),
            0);

        var reconciled = await repo.ReconcileStaleAsync(
            session.Id, "owner-1", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);

        reconciled!.Status.Should().Be(UploadSessionStatuses.Completed);
        reconciled.Items.Should().OnlyContain(x => x.Status == UploadItemStatus.Interrupted);
    }

    [Fact]
    public async Task ClientTransportExhaustion_IsDurablyTerminal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repo = new UploadSessionRepository(fixture.Context);
        var session = Session();
        var item = Item(session.Id, "item-1", Guid.NewGuid().ToString(), 0);
        await repo.CreateAsync(session, [item], Event(session), 0);
        var detached = await repo.GetItemAsync(session.Id, item.Id, "owner-1");
        detached!.TransitionTo(
            UploadItemStatus.Failed,
            DateTime.UtcNow,
            "retry_exhausted",
            "Upload failed after four browser transport attempts.");

        await repo.UpdateItemAsync(detached, 1, [], CancellationToken.None);

        var persisted = await repo.GetOwnedAsync(session.Id, "owner-1");
        persisted!.Status.Should().Be(UploadSessionStatuses.Completed);
        persisted.FailedCount.Should().Be(1);
        persisted.TerminalItemCount.Should().Be(1);
        var stored = persisted.Items.Single();
        stored.Status.Should().Be(UploadItemStatus.Failed);
        stored.OutcomeCode.Should().Be("retry_exhausted");
        stored.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HeartbeatRenewsLeaseDespiteConcurrentItemVersionChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repo = new UploadSessionRepository(fixture.Context);
        var session = Session();
        session.LastHeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
        var item = Item(session.Id, "item-1", Guid.NewGuid().ToString(), 0);
        await repo.CreateAsync(session, [item], Event(session), 0);
        var originalSessionVersion = session.ConcurrencyVersion;
        var detached = await repo.GetItemAsync(session.Id, item.Id, "owner-1");
        detached!.TransitionTo(UploadItemStatus.Throttled, DateTime.UtcNow);
        await repo.UpdateItemAsync(detached, 1, [], CancellationToken.None);
        var renewedAt = DateTime.UtcNow;

        await repo.HeartbeatAsync(
            session.Id,
            "owner-1",
            originalSessionVersion,
            renewedAt,
            Event(session),
            CancellationToken.None);
        var reconciled = await repo.ReconcileStaleAsync(
            session.Id,
            "owner-1",
            renewedAt.AddSeconds(-1),
            renewedAt.AddSeconds(1));

        reconciled!.Items.Single().Status.Should().Be(UploadItemStatus.Throttled);
        reconciled.LastHeartbeatAt.Should().Be(renewedAt);
    }

    [Fact]
    public async Task ReconciliationToleratesShortHeartbeatGapsDuringLargeUploads()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repo = new UploadSessionRepository(fixture.Context);
        var now = DateTime.UtcNow;
        var session = Session();
        session.LastHeartbeatAt = now.AddSeconds(-30);
        var item = Item(session.Id, "item-1", Guid.NewGuid().ToString(), 0);
        await repo.CreateAsync(session, [item], Event(session), 0);

        var reconciled = await repo.ReconcileStaleAsync(
            session.Id,
            "owner-1",
            now - TalentMatch.Application.Uploads.Services.UploadItemLifecycleService.HeartbeatLease,
            now);

        reconciled!.Status.Should().Be("active");
        reconciled.Items.Single().Status.Should().Be(UploadItemStatus.Waiting);
    }

    [Fact]
    public async Task CompletedUploadCreatesQueuedApplicationAtomically()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repo = new UploadSessionRepository(fixture.Context);
        var session = Session();
        var occurrence = Guid.NewGuid().ToString();
        var item = Item(session.Id, "item-1", occurrence, 0);
        await repo.CreateAsync(session, [item], Event(session), 0);
        var detached = await repo.GetItemAsync(session.Id, item.Id, "owner-1");
        detached!.TransitionTo(UploadItemStatus.Uploading, DateTime.UtcNow);
        await repo.UpdateItemAsync(detached, 1, [], CancellationToken.None);

        var completed = await repo.CompleteItemAsync(
            session.Id,
            item.Id,
            "owner-1",
            occurrence,
            "fingerprint-1",
            "candidate.md",
            "text/markdown",
            2,
            Convert.ToBase64String("cv"u8),
            CancellationToken.None);

        completed.Status.Should().Be(UploadItemStatus.Succeeded);
        (await fixture.Context.Applications.AsNoTracking().SingleAsync())
            .Status.Should().Be("Queued");
    }

    [Fact]
    public async Task SessionCreation_RejectsASettingsSnapshotThatLostTheCommitRace()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Context.UploadSettings.Add(new UploadSettings
        {
            FileConcurrency = 2,
            MaxIndividualFileBytes = 20_000_000,
            MaxInFlightBytes = 40_000_000,
            ConcurrencyVersion = 1,
            CreatedBy = "admin-1",
            UpdatedBy = "admin-1",
        });
        await fixture.Context.SaveChangesAsync();
        var repo = new UploadSessionRepository(fixture.Context);
        var session = Session();

        var act = () => repo.CreateAsync(
            session,
            [Item(session.Id, "item-1", Guid.NewGuid().ToString(), 0)],
            Event(session),
            expectedSettingsConcurrencyVersion: 0);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("settings_stale");
        (await fixture.Context.UploadSessions.CountAsync()).Should().Be(0);
    }

    private static UploadSession Session() => new()
    {
        Id = Guid.NewGuid().ToString(),
        JobId = "job-1",
        OwnerActorId = "owner-1",
        FileConcurrency = 4,
        MaxIndividualFileBytes = 4_194_304,
        MaxInFlightBytes = 104_857_600,
    };
    private static UploadItem Item(string sessionId, string id, string occurrence, int ordinal) => new()
    {
        Id = id,
        SessionId = sessionId,
        OccurrenceKey = occurrence,
        Ordinal = ordinal,
        FileName = "candidate.pdf",
        MimeType = "application/pdf",
        RawSizeBytes = 2,
    };
    private static ProcessingEvent Event(UploadSession session) => new()
    {
        EventType = "upload-session.created",
        EntityType = nameof(UploadSession),
        EntityId = session.Id,
        Actor = "owner-1",
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Context { get; }
        private Fixture(SqliteConnection connection, AppDbContext context)
        {
            _connection = connection;
            Context = context;
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            context.Jobs.Add(new Job { Id = "job-1", Title = "Job" });
            await context.SaveChangesAsync();
            return new(connection, context);
        }
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
