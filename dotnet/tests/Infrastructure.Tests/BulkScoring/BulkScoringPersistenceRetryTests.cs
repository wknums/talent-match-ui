using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringRepositoryTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkScoringPersistenceRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Content_staging_replays_without_inserting_the_existing_parent(bool afterSave)
    {
        var failure = new FailOnce(afterSave);
        await using var f = await Fixture.CreateAsync(configure: options => options
            .AddInterceptors(failure)
            .ReplaceService<IExecutionStrategyFactory, DeadlockStrategyFactory>());
        var request = await f.ActiveAsync("stage", 1, null);
        var item = (await f.Repo.ClaimDueItemsAsync("worker", 1, Now, TimeSpan.FromMinutes(5))).Single();
        item.SourceStoredAt = Now;
        var content = new BulkScoringItemContent
        {
            ItemId = item.Id,
            Kind = BulkContentKind.Original,
            ContentBase64 = "AQ==",
            SizeBytes = 1,
            Sha256 = "test"
        };
        failure.Armed = true;

        await f.Repo.SaveItemAndContentsWithAuditsAsync(
            item, [content], [Audit(request, item, BulkAuditEventTypes.BulkOriginalContentStaged)]);

        failure.Failures.Should().Be(1);
        (await f.Db.BulkScoringItems.CountAsync()).Should().Be(1);
        (await f.Db.BulkScoringItemContents.CountAsync()).Should().Be(1);
        (await f.Db.BulkAuditEvents.CountAsync(e => e.SubjectId == item.Id
            && e.EventType == BulkAuditEventTypes.BulkOriginalContentStaged)).Should().Be(1);
        (await f.Repo.GetItemAsync(request.Id, item.Id))!.SourceStoredAt.Should().Be(Now);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handoff_replays_and_repeated_delivery_does_not_requeue_scoring(bool typedAudit)
    {
        var failure = new FailOnce(afterSave: true);
        await using var f = await Fixture.CreateAsync(migrate: false, configure: options => options
            .AddInterceptors(failure)
            .ReplaceService<IExecutionStrategyFactory, DeadlockStrategyFactory>());
        var request = await f.ActiveAsync("handoff", 1, null);
        var item = (await f.Repo.ClaimDueItemsAsync("worker", 1, Now, TimeSpan.FromMinutes(5))).Single();
        f.Db.Applications.Add(new TalentMatch.Domain.Entities.Application
        {
            Id = "app-retry", JobId = request.JobId, CandidateRef = "candidate", Status = "Uploading"
        });
        await f.Db.SaveChangesAsync();
        item.LinkApplication("app-retry", "document-retry", Now);
        await f.Repo.SaveItemAsync(item);
        await f.Db.BulkScoringRequests.Where(r => r.Id == request.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CapturedScoringConfigVersionId, "config-retry"));
        failure.Armed = true;

        Task<string> Handoff() => typedAudit
            ? f.Repo.HandoffToScoringAsync(
                item, Audit(request, item, BulkAuditEventTypes.BulkItemScoringHandoffQueued), Now)
            : f.Repo.HandoffToScoringAsync(item, new ProcessingEvent
            {
                EntityType = "bulk_item", EntityId = item.Id, EventType = "handoff"
            }, Now);
        (await Handoff()).Should().Be("config-retry");
        item.Status.Should().Be(BulkItemStatus.Scoring);
        (await f.Repo.GetAsync(request.Id))!.ScoringCount.Should().Be(1);
        await f.Db.Applications.Where(a => a.Id == "app-retry")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Scoring"));

        (await Handoff()).Should().Be("config-retry");

        failure.Failures.Should().Be(1);
        (await f.Db.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Scoring");
        if (typedAudit)
            (await f.Db.BulkAuditEvents.CountAsync(e => e.SubjectId == item.Id
                && e.EventType == BulkAuditEventTypes.BulkItemScoringHandoffQueued)).Should().Be(1);
        else
            (await f.Db.ProcessingEvents.CountAsync(e => e.EntityId == item.Id
                && e.EventType == "handoff")).Should().Be(1);
    }

    [Fact]
    public async Task Audit_only_writes_do_not_recount_but_status_changes_still_reconcile_atomically()
    {
        var queries = new CountQueries();
        await using var f = await Fixture.CreateAsync(configure: options => options.AddInterceptors(queries));
        var request = await f.ActiveAsync("counts", 1, null);
        var item = (await f.Repo.ClaimDueItemsAsync("worker", 1, Now, TimeSpan.FromMinutes(5))).Single();
        queries.Count = 0;

        await f.Repo.EnsureItemAuditAsync(item, Audit(request, item, BulkAuditEventTypes.BulkItemClaimed));
        await f.Repo.SaveItemWithAuditAsync(item, Audit(request, item, BulkAuditEventTypes.BulkItemImportStarted));
        queries.Count.Should().Be(0);

        item.TransitionTo(BulkItemStatus.Converting, Now);
        await f.Repo.SaveItemWithAuditAsync(item, Audit(request, item, BulkAuditEventTypes.BulkItemConversionStarted));
        queries.Count.Should().Be(1);
        var updated = (await f.Repo.GetAsync(request.Id))!;
        (updated.ImportingCount, updated.ConvertingCount).Should().Be((0, 1));
        (await f.Db.BulkAuditEvents.CountAsync(e => e.SubjectId == item.Id
            && e.EventType == BulkAuditEventTypes.BulkItemConversionStarted)).Should().Be(1);
    }

    internal static BulkAuditEvent Audit(BulkScoringRequest request, BulkScoringItem item, string type) =>
        BulkAuditWriter.Service(type, "test-worker", request.OwnerActorId, request.CorrelationId,
            Now, "bulk_item", item.Id, new { state = "persisted", attempt = item.Attempt });

    private sealed class DeadlockStrategyFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new SequentialScoringExecutionStrategy(dependencies.CurrentContext.Context);
    }

    private sealed class FailOnce(bool afterSave) : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }

        private void Fail()
        {
            if (!Armed || Failures != 0) return;
            Failures++;
            throw SqlServerTestErrors.Create(1205);
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!afterSave) Fail();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (afterSave) Fail();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountQueries : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GROUP BY", StringComparison.Ordinal)
                && command.CommandText.Contains("BulkScoringItems", StringComparison.Ordinal))
                Count++;
            return ValueTask.FromResult(result);
        }
    }
}
