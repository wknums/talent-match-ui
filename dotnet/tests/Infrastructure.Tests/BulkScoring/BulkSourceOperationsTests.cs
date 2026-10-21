using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Web.Server.Operations;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringRepositoryTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkSourceOperationsTests
{
    [Fact]
    public async Task Preflight_inventories_active_draft_authorization_and_completed_history_without_mutating_them()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var active = await ActiveAsync(fixture, "active");
        var completed = await ActiveAsync(fixture, "completed");
        var draft = await fixture.Repo.CreateDraftAsync(NewRequest(fixture), Step(Items(1, "draft")));
        fixture.Db.BulkRequestAuthorizations.Add(Authorization(active.Id));
        completed.WaitingCount = 0;
        completed.CompletedCount = completed.DiscoveredCount;
        completed.Complete(Now);
        fixture.Db.Update(completed);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var before = await SnapshotAsync(fixture.Db);
        var inventory = await new BulkSourceMigrationPreflight(fixture.Db, new FixedClock(Now)).InventoryAsync();

        inventory.ActiveRequests.Should().Be(1);
        inventory.DraftRequests.Should().Be(1);
        inventory.ActiveAuthorizations.Should().Be(1);
        inventory.CompletedHistoryRequests.Should().Be(1);
        inventory.IsDrained.Should().BeFalse();
        (await SnapshotAsync(fixture.Db)).Should().BeEquivalentTo(before);
        (await fixture.Repo.GetAsync(draft.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Preflight_refuses_schema_change_until_old_work_is_drained_and_does_not_translate_authorization()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var active = await ActiveAsync(fixture, "active");
        fixture.Db.BulkRequestAuthorizations.Add(Authorization(active.Id));
        await fixture.Db.SaveChangesAsync();
        var preflight = new BulkSourceMigrationPreflight(fixture.Db, new FixedClock(Now));

        await preflight.Invoking(value => value.RequireDrainedAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires_drain_or_discard*");

        var authorization = await fixture.Db.BulkRequestAuthorizations.SingleAsync();
        authorization.Status.Should().Be(BulkAuthorizationStatus.Active);
        authorization.Payload.Should().Equal(SecretPayload);
        authorization.WrappedDataKey.Should().Equal(SecretKey);
    }

    [Fact]
    public async Task Explicit_discard_clears_protected_material_records_a_safe_immutable_event_and_still_requires_request_drain()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var active = await ActiveAsync(fixture, "active");
        fixture.Db.BulkRequestAuthorizations.Add(Authorization(active.Id));
        await fixture.Db.SaveChangesAsync();
        var preflight = new BulkSourceMigrationPreflight(fixture.Db, new FixedClock(Now));

        await preflight.Invoking(value => value.DiscardOldAuthorizationMaterialAsync(false))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*operator confirmation*");
        (await preflight.DiscardOldAuthorizationMaterialAsync(true)).Should().Be(1);

        fixture.Db.ChangeTracker.Clear();
        var discarded = await fixture.Db.BulkRequestAuthorizations.SingleAsync();
        discarded.Status.Should().Be(BulkAuthorizationStatus.Discarded);
        discarded.Payload.Should().BeNull();
        discarded.WrappedDataKey.Should().BeNull();
        discarded.DiscardedAt.Should().Be(Now);
        await preflight.Invoking(value => value.RequireDrainedAsync())
            .Should().ThrowAsync<InvalidOperationException>("discard is not permission to abandon active work");

        var audit = await fixture.Db.BulkAuditEvents.SingleAsync(
            value => value.EventType == BulkAuditEventTypes.RequestAuthorizationDiscarded);
        audit.ActorType.Should().Be(BulkAuditActorType.Service);
        audit.ActorId.Should().Be("bulk-source-migration-service");
        audit.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        audit.Boundary.Should().Be(BulkAuditBoundary.Transactional);
        audit.Validate();
        Serialized(audit).Should().NotContainAny(ProhibitedPayloads);
    }

    [Fact]
    public async Task Completed_history_remains_readable_after_inventory_and_protected_material_discard()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var completed = await ActiveAsync(fixture, "completed");
        completed.WaitingCount = 0;
        completed.CompletedCount = completed.DiscoveredCount;
        completed.Complete(Now);
        fixture.Db.Update(completed);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var preflight = new BulkSourceMigrationPreflight(fixture.Db, new FixedClock(Now));
        var inventory = await preflight.RequireDrainedAsync();

        inventory.CompletedHistoryRequests.Should().Be(1);
        var history = await fixture.Repo.GetAsync(completed.Id);
        history.Should().NotBeNull();
        history!.Status.Should().Be(BulkRequestStatus.Completed);
        history.Outcome.Should().Be(BulkRequestOutcome.AllSucceeded);
        (await fixture.Repo.GetItemsAsync(completed.Id, new BulkItemQuery(1, 10))).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Authorization_telemetry_accepts_only_safe_dimensions_and_never_records_provider_or_source_values()
    {
        var telemetryType = typeof(TalentMatch.Infrastructure.DependencyInjection).Assembly.GetType(
            "TalentMatch.Infrastructure.SharePoint.Authorization.SourceAuthorizationTelemetry");
        telemetryType.Should().NotBeNull(
            "T095 must centralize safe source-authorization metrics and logs before the old runtime is removed");

        var events = new RecordingEvents();
        var logger = new CapturingLogger();
        var telemetry = new BulkTelemetry(events, logger, new FixedClock(Now));
        const string unsafeReason =
            "interaction_required https://tenant.example/folder jane@example.com access_token=secret";

        await telemetry.AuthorizationAsync(
            "lapsed",
            "request-opaque",
            "job-opaque",
            "bulk-authorization-lapse-service",
            unsafeReason,
            2,
            Guid.NewGuid().ToString());

        var evidence = logger.Text + "\n" + JsonSerializer.Serialize(events.Items);
        evidence.Should().NotContainAny(ProhibitedPayloads);
        events.Items.Should().ContainSingle();
        events.Items[0].PayloadJson.Should().Contain("\"factVersion\":1");
    }

    private static readonly byte[] SecretPayload = [11, 12, 13];
    private static readonly byte[] SecretKey = [21, 22, 23];
    private static readonly string[] ProhibitedPayloads =
    [
        "https://tenant.example/folder",
        "jane@example.com",
        "access_token",
        "secret",
        "Candidates",
        "SecretPayload",
        "SecretKey",
    ];

    private static BulkRequestAuthorization Authorization(string requestId) =>
        BulkRequestAuthorization.Establish(
            requestId,
            "actor",
            "source-tenant",
            "account-binding",
            "drv",
            "fld",
            SecretPayload.ToArray(),
            SecretKey.ToArray(),
            "v1",
            Now);

    private static BulkScoringRequest NewRequest(Fixture fixture)
    {
        var request = fixture.NewRequest();
        request.MaxFilesPerRequest = 30_000;
        request.MaxBytesPerFile = 8_388_608;
        request.MaxTotalBytes = 53_687_091_200;
        request.DiscoveryStepMaxFiles = 1_000;
        request.DiscoveryStepMaxEntries = 10_000;
        request.DiscoveryStepMaxSeconds = 45;
        return request;
    }

    private static async Task<BulkScoringRequest> ActiveAsync(Fixture fixture, string prefix)
    {
        var draft = await fixture.Repo.CreateDraftAsync(
            NewRequest(fixture),
            Step(Items(1, prefix)));
        var active = await fixture.Repo.ConfirmAsync(
            draft.Id,
            draft.ConcurrencyVersion,
            false,
            false,
            null,
            null,
            Now);
        fixture.Db.ChangeTracker.Clear();
        return active;
    }

    private static async Task<object> SnapshotAsync(TalentMatch.Infrastructure.Persistence.AppDbContext context) =>
        new
        {
            Requests = await context.BulkScoringRequests.OrderBy(value => value.Id)
                .Select(value => new { value.Id, value.Status, value.Outcome }).ToListAsync(),
            Authorizations = await context.BulkRequestAuthorizations.OrderBy(value => value.RequestId)
                .Select(value => new { value.RequestId, value.Status }).ToListAsync(),
        };

    private static string Serialized(BulkAuditEvent value) => JsonSerializer.Serialize(value);

    private sealed class RecordingEvents : IProcessingEventRepository
    {
        public List<ProcessingEvent> Items { get; } = [];

        public Task AddAsync(ProcessingEvent evt, CancellationToken cancellationToken = default)
        {
            Items.Add(evt);
            return Task.CompletedTask;
        }

        public Task AddAuthorizationEventAsync(
            string actor,
            string action,
            string subjectId,
            IReadOnlyDictionary<string, object?> safeDetails,
            string correlationId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ProcessingEvent>> GetFilteredAsync(
            string? entityType = null,
            string? eventType = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            int page = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessingEvent>>(Items);
    }

    private sealed class CapturingLogger : ILogger<BulkTelemetry>
    {
        public string Text { get; private set; } = string.Empty;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Text += formatter(state, exception) + "\n";
    }
}
