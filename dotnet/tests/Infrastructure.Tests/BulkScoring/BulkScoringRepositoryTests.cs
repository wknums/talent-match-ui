using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkScoringRepositoryTests
{
    internal static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Draft_creation_is_atomic_with_its_first_step()
    {
        await using var f = await Fixture.CreateAsync();

        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(3, "a"), new() { [BulkReasonCode.UnsupportedType] = 2 }, inaccessible: 1, entries: 7));

        draft.Status.Should().Be(BulkRequestStatus.Draft);
        draft.DiscoveredCount.Should().Be(3);
        draft.WaitingCount.Should().Be(3);
        draft.ExcludedCount.Should().Be(2);
        draft.InaccessibleCount.Should().Be(1);
        draft.EntriesScanned.Should().Be(7);
        draft.DiscoveryStepCount.Should().Be(1);
        draft.ExpiresAt.Should().Be(Now.AddMinutes(30));
        (await f.Db.BulkScoringItems.CountAsync()).Should().Be(3);
        (await f.Repo.GetAsync(draft.Id))!.TotalEligibleBytes.Should().Be(30);
    }

    [Fact]
    public async Task A_discovery_step_adds_items_ignores_relisted_duplicates_and_floors_reversed_tallies()
    {
        await using var f = await Fixture.CreateAsync();
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(3, "a"), new() { [BulkReasonCode.UnsupportedType] = 2 }, entries: 5, status: BulkDiscoveryStatus.MoreAvailable));

        var next = await f.Repo.ApplyDiscoveryStepAsync(draft.Id, draft.ConcurrencyVersion,
            Step(Items(2, "a").Concat(Items(2, "b")).ToList(), new() { [BulkReasonCode.UnsupportedType] = -5 }, entries: -5));

        next.DiscoveredCount.Should().Be(5, "a0 and a1 were already stored");
        next.ExcludedCount.Should().Be(0, "a reversal never goes below zero");
        next.EntriesScanned.Should().Be(0);
        next.DiscoveryStepCount.Should().Be(2);
        next.ConcurrencyVersion.Should().Be(draft.ConcurrencyVersion + 1);
        (await f.Db.BulkScoringItems.CountAsync()).Should().Be(5);
    }

    [Fact]
    public async Task A_discovery_step_rejects_a_stale_version_and_a_finished_discovery()
    {
        await using var f = await Fixture.CreateAsync();
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(1, "a"), status: BulkDiscoveryStatus.Complete));

        await f.Repo.Invoking(r => r.ApplyDiscoveryStepAsync(draft.Id, 99, Step(Items(1, "b"))))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("stale_version");
        await f.Repo.Invoking(r => r.ApplyDiscoveryStepAsync(draft.Id, draft.ConcurrencyVersion, Step(Items(1, "b"))))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Confirm_drops_files_that_cannot_be_rasterized_and_counts_them_as_excluded()
    {
        await using var f = await Fixture.CreateAsync();
        var items = Items(4, "a");
        items[0].ExcludedWhenRasterized = true;
        items[1].ExcludedWhenRasterized = true;
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(items));

        var active = await f.Repo.ConfirmAsync(draft.Id, draft.ConcurrencyVersion, convertToRasterPdf: true, allowDuplicates: false, "cfg-1", null, Now);

        active.Status.Should().Be(BulkRequestStatus.Active);
        active.ConvertToRasterPdf.Should().BeTrue();
        active.DiscoveredCount.Should().Be(2);
        active.ExcludedCount.Should().Be(2);
        active.GetExcludedByReason()[BulkReasonCode.NotConvertible].Should().Be(2);
        active.TotalEligibleBytes.Should().Be(20);
        active.JobConfigVersionId.Should().Be("cfg-1");
        active.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Confirm_with_the_option_off_keeps_every_file()
    {
        await using var f = await Fixture.CreateAsync();
        var items = Items(2, "a");
        items[0].ExcludedWhenRasterized = true;
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(items));

        var active = await f.Repo.ConfirmAsync(draft.Id, draft.ConcurrencyVersion, false, false, null, null, Now);

        active.DiscoveredCount.Should().Be(2);
        active.ExcludedCount.Should().Be(0);
    }

    [Fact]
    public async Task Confirm_stores_the_authorization_in_the_same_transaction_as_activation()
    {
        await using var f = await Fixture.CreateAsync();
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(1, "a")));
        var auth = BulkRequestAuthorization.Establish(
            draft.Id,
            draft.OwnerActorId,
            draft.SourceTenantId,
            draft.SourceAccountBindingHash,
            draft.SourceDriveId,
            draft.SourceFolderItemId,
            [1, 2],
            [3],
            "v1",
            Now);

        await f.Repo.ConfirmAsync(draft.Id, draft.ConcurrencyVersion, false, false, null, auth, Now);

        (await f.Db.BulkRequestAuthorizations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Confirm_that_leaves_no_file_rolls_everything_back()
    {
        await using var f = await Fixture.CreateAsync();
        var items = Items(2, "a");
        items.ForEach(i => i.ExcludedWhenRasterized = true);
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(items));

        await f.Repo.Invoking(r => r.ConfirmAsync(draft.Id, draft.ConcurrencyVersion, true, false, null, null, Now))
            .Should().ThrowAsync<InvalidOperationException>();

        f.Db.ChangeTracker.Clear();
        (await f.Db.BulkScoringItems.CountAsync()).Should().Be(2, "the delete was rolled back");
        (await f.Repo.GetAsync(draft.Id))!.Status.Should().Be(BulkRequestStatus.Draft);
    }

    [Fact]
    public async Task Confirm_accepts_a_fully_bound_delegated_authorization_for_another_organizational_tenant()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.NewRequest();
        request.SourceTenantId = "other-organizational-tenant";
        var draft = await f.Repo.CreateDraftAsync(request, Step(Items(1, "a")));
        var auth = BulkRequestAuthorization.Establish(
            draft.Id,
            request.OwnerActorId,
            request.SourceTenantId,
            request.SourceAccountBindingHash,
            request.SourceDriveId,
            request.SourceFolderItemId,
            [1],
            [1],
            "v1",
            Now);

        var active = await f.Repo.ConfirmAsync(
            draft.Id,
            draft.ConcurrencyVersion,
            false,
            false,
            null,
            auth,
            Now);

        active.Status.Should().Be(BulkRequestStatus.Active);
        (await f.Db.BulkRequestAuthorizations.SingleAsync()).SourceTenantId
            .Should().Be("other-organizational-tenant");
    }

    [Fact]
    public async Task The_filtered_unique_fingerprint_serializes_duplicates_within_a_request()
    {
        await using var f = await Fixture.CreateAsync();
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(3, "a")));
        var ids = await f.Db.BulkScoringItems.OrderBy(i => i.Ordinal).Select(i => i.Id).ToListAsync();
        f.Db.ChangeTracker.Clear();

        (await f.Repo.TryClaimFingerprintAsync(ids[0], "hash-1")).Should().BeTrue();
        (await f.Repo.TryClaimFingerprintAsync(ids[1], "hash-1")).Should().BeFalse();
        (await f.Repo.TryClaimFingerprintAsync(ids[1], "hash-2")).Should().BeTrue();
        draft.Id.Should().NotBeNull();
    }

    [Fact]
    public async Task Claiming_is_fair_across_all_delegated_requests()
    {
        await using var f = await Fixture.CreateAsync();
        var a = await f.ActiveAsync("a", 6, owningStack: "B");
        var b = await f.ActiveAsync("b", 6, owningStack: "B");
        var otherTenant = await f.ActiveAsync("c", 6, owningStack: "A");
        var workOneDrive = await f.ActiveAsync("d", 2, owningStack: null, cross: true);

        var claimed = await f.Repo.ClaimDueItemsAsync("worker-1", 6, Now, TimeSpan.FromMinutes(2));

        claimed.Should().HaveCount(6);
        claimed.GroupBy(i => i.RequestId).Select(g => g.Count()).Should().OnlyContain(n => n >= 1);
        claimed.Select(i => i.RequestId).Should().Contain(
            [a.Id, b.Id, otherTenant.Id, workOneDrive.Id],
            "delegated requests have no owning-stack selector");
        claimed.Should().OnlyContain(i => i.Status == BulkItemStatus.Importing && i.LeaseOwner == "worker-1" && i.Attempt == 1);
        (await f.Repo.GetAsync(a.Id))!.ImportingCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_item()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ActiveAsync("a", 10, owningStack: "B");

        var first = await f.Repo.ClaimDueItemsAsync("w1", 6, Now, TimeSpan.FromMinutes(2));
        var second = await f.Repo.ClaimDueItemsAsync("w2", 6, Now, TimeSpan.FromMinutes(2));

        first.Select(i => i.Id).Intersect(second.Select(i => i.Id)).Should().BeEmpty();
        (first.Count + second.Count).Should().Be(10);
    }

    [Fact]
    public async Task Items_waiting_for_a_retry_backoff_and_cancelled_requests_are_not_claimed()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 2, owningStack: "B");
        await f.Db.BulkScoringItems.Where(i => i.RequestId == request.Id && i.Ordinal == 0)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.NextAttemptAt, (DateTime?)Now.AddMinutes(5)));

        var claimed = await f.Repo.ClaimDueItemsAsync("w", 5, Now, TimeSpan.FromMinutes(1));
        claimed.Should().ContainSingle().Which.Ordinal.Should().Be(1);

        await f.Repo.RequestCancelAsync(request.Id, (await f.Repo.GetAsync(request.Id))!.ConcurrencyVersion, "actor", Now);
        (await f.Repo.ClaimDueItemsAsync("w", 5, Now.AddMinutes(10), TimeSpan.FromMinutes(1))).Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_leases_return_to_waiting_and_a_graceful_release_does_not_consume_an_attempt()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 2, owningStack: "B");
        await f.Repo.ClaimDueItemsAsync("w1", 2, Now, TimeSpan.FromMinutes(1));

        (await f.Repo.ReclaimExpiredLeasesAsync(Now.AddSeconds(30))).Should().Be(0);
        (await f.Repo.ReclaimExpiredLeasesAsync(Now.AddMinutes(2))).Should().Be(2);
        (await f.Repo.GetAsync(request.Id))!.WaitingCount.Should().Be(2);
        (await f.Db.BulkScoringItems.AsNoTracking().ToListAsync()).Should().OnlyContain(i => i.Attempt == 1 && i.LeaseOwner == null);

        await f.Repo.ClaimDueItemsAsync("w2", 1, Now.AddMinutes(3), TimeSpan.FromMinutes(1));
        await f.Repo.ReleaseLeasesAsync("w2");
        (await f.Db.BulkScoringItems.AsNoTracking().OrderBy(i => i.Ordinal).ToListAsync()).Should().OnlyContain(i => i.Status == BulkItemStatus.Waiting);
        (await f.Db.BulkScoringItems.AsNoTracking().Select(i => i.Attempt).ToListAsync()).Should().OnlyContain(a => a == 1);
    }

    [Fact]
    public async Task Saving_an_item_updates_the_request_counts_and_the_request_finalizes_when_all_items_are_terminal()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 2, owningStack: "B");
        var claimed = await f.Repo.ClaimDueItemsAsync("w", 2, Now, TimeSpan.FromMinutes(1));

        claimed[0].LinkApplication("app-1", "doc-1", Now);
        claimed[0].TransitionTo(BulkItemStatus.Scoring, Now);
        await f.Repo.SaveItemAsync(claimed[0]);
        (await f.Repo.TryFinalizeAsync(request.Id, Now)).Should().BeNull("one item is still importing and one is scoring");
        (await f.Repo.GetAsync(request.Id))!.ScoringCount.Should().Be(1);

        claimed[0].TransitionTo(BulkItemStatus.Completed, Now);
        await f.Repo.SaveItemAsync(claimed[0]);
        claimed[1].TransitionTo(BulkItemStatus.Failed, Now, BulkReasonCode.SourceMissing, "gone");
        await f.Repo.SaveItemAsync(claimed[1]);

        var done = await f.Repo.TryFinalizeAsync(request.Id, Now.AddMinutes(1));

        done!.Status.Should().Be(BulkRequestStatus.Completed);
        done.Outcome.Should().Be(BulkRequestOutcome.Partial);
        done.TerminalAt.Should().Be(Now.AddMinutes(1));
        (await f.Repo.TryFinalizeAsync(request.Id, Now)).Should().BeNull("already completed");
        var stored = await f.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == claimed[0].Id);
        stored.ApplicationId.Should().Be("app-1");
    }

    [Fact]
    public async Task Finalizing_corrects_count_drift()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 2, owningStack: "B");
        await f.Db.BulkScoringRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.WaitingCount, 0).SetProperty(r => r.CompletedCount, 2));

        (await f.Repo.TryFinalizeAsync(request.Id, Now)).Should().BeNull();

        var fixedUp = (await f.Repo.GetAsync(request.Id))!;
        fixedUp.WaitingCount.Should().Be(2);
        fixedUp.CompletedCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_stops_waiting_files_lets_scoring_files_finish_and_completes_when_nothing_remains()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 3, owningStack: "B");
        var claimed = (await f.Repo.ClaimDueItemsAsync("w", 1, Now, TimeSpan.FromMinutes(1)))[0];
        claimed.LinkApplication("app", "doc", Now);
        claimed.TransitionTo(BulkItemStatus.Scoring, Now);
        await f.Repo.SaveItemAsync(claimed);

        var cancelled = await f.Repo.RequestCancelAsync(request.Id, (await f.Repo.GetAsync(request.Id))!.ConcurrencyVersion, "actor", Now);

        cancelled.Status.Should().Be(BulkRequestStatus.Active, "one file is still being scored");
        cancelled.CancelledCount.Should().Be(2);
        cancelled.ScoringCount.Should().Be(1);

        var item = await f.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == claimed.Id);
        item.TransitionTo(BulkItemStatus.Completed, Now);
        await f.Repo.SaveItemAsync(item);
        var done = await f.Repo.TryFinalizeAsync(request.Id, Now);
        done!.Outcome.Should().Be(BulkRequestOutcome.Cancelled);
        (await f.Db.BulkScoringItems.AsNoTracking().Where(i => i.Status == BulkItemStatus.Cancelled).ToListAsync())
            .Should().OnlyContain(i => i.ReasonCode == BulkReasonCode.CancelledByUser);
    }

    [Fact]
    public async Task Cancelling_a_request_with_only_waiting_files_completes_it_immediately()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 2, owningStack: "B");

        var done = await f.Repo.RequestCancelAsync(request.Id, request.ConcurrencyVersion, "actor", Now);

        done.Status.Should().Be(BulkRequestStatus.Completed);
        done.Outcome.Should().Be(BulkRequestOutcome.Cancelled);
        await f.Repo.Invoking(r => r.RequestCancelAsync(request.Id, 1, "actor", Now)).Should().ThrowAsync<InvalidOperationException>().WithMessage("stale_version");
    }

    [Fact]
    public async Task Failing_non_terminal_items_leaves_scoring_and_finished_items_alone()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 4, owningStack: "B");
        var claimed = await f.Repo.ClaimDueItemsAsync("w", 2, Now, TimeSpan.FromMinutes(1));
        claimed[0].LinkApplication("app", "doc", Now);
        claimed[0].TransitionTo(BulkItemStatus.Scoring, Now);
        await f.Repo.SaveItemAsync(claimed[0]);

        var failed = await f.Repo.FailNonTerminalItemsAsync(request.Id, BulkReasonCode.AuthorizationLapsed, "Sign in again.", Now);

        failed.Should().Be(3, "one importing and two waiting");
        var items = await f.Db.BulkScoringItems.AsNoTracking().OrderBy(i => i.Ordinal).ToListAsync();
        items.Count(i => i.Status == BulkItemStatus.Failed && i.ReasonCode == BulkReasonCode.AuthorizationLapsed).Should().Be(3);
        items.Count(i => i.Status == BulkItemStatus.Scoring).Should().Be(1);
        (await f.Repo.GetRetryableItemsAsync(request.Id)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Retryable_items_exclude_scoring_stage_failures_and_non_retryable_reasons()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 4, owningStack: "B");
        var claimed = await f.Repo.ClaimDueItemsAsync("w", 4, Now, TimeSpan.FromMinutes(1));
        claimed[0].TransitionTo(BulkItemStatus.Failed, Now, BulkReasonCode.TransientExhausted);
        claimed[1].TransitionTo(BulkItemStatus.Failed, Now, BulkReasonCode.ConversionFailed);
        claimed[2].LinkApplication("a", "d", Now);
        claimed[2].TransitionTo(BulkItemStatus.Scoring, Now);
        claimed[2].TransitionTo(BulkItemStatus.Failed, Now, BulkReasonCode.ScoringFailed);
        claimed[3].TransitionTo(BulkItemStatus.Cancelled, Now);
        foreach (var item in claimed) await f.Repo.SaveItemAsync(item);

        var retryable = await f.Repo.GetRetryableItemsAsync(request.Id);

        retryable.Select(i => i.ReasonCode).Should().BeEquivalentTo([BulkReasonCode.TransientExhausted, BulkReasonCode.CancelledByUser]);
    }

    [Fact]
    public async Task Item_paging_filters_by_status_and_name_and_reports_retained_originals()
    {
        await using var f = await Fixture.CreateAsync();
        var items = Items(5, "cv");
        items[3].FileName = "special_100%.pdf";
        var draft = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(items));
        var first = await f.Db.BulkScoringItems.AsNoTracking().OrderBy(i => i.Ordinal).FirstAsync();
        await f.Repo.AddContentAsync(new BulkScoringItemContent { ItemId = first.Id, Kind = BulkContentKind.Original, ContentBase64 = "AAAA", SizeBytes = 3, Sha256 = "x" });

        var page1 = await f.Repo.GetItemsAsync(draft.Id, new BulkItemQuery(1, 2));
        var page3 = await f.Repo.GetItemsAsync(draft.Id, new BulkItemQuery(3, 2));
        var named = await f.Repo.GetItemsAsync(draft.Id, new BulkItemQuery(1, 10, NameContains: "100%"));
        var byStatus = await f.Repo.GetItemsAsync(draft.Id, new BulkItemQuery(1, 10, Status: BulkItemStatus.Completed));

        page1.Total.Should().Be(5);
        page1.Items.Select(i => i.Ordinal).Should().Equal(0, 1);
        page1.Items[0].HasRetainedOriginal.Should().BeTrue();
        page1.Items[1].HasRetainedOriginal.Should().BeFalse();
        page3.Items.Should().ContainSingle().Which.Ordinal.Should().Be(4);
        named.Items.Should().ContainSingle().Which.FileName.Should().Be("special_100%.pdf");
        byStatus.Total.Should().Be(0);
    }

    [Fact]
    public async Task Content_is_replaced_idempotently_and_removed_by_kind()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(1, "a")));
        var id = await f.Db.BulkScoringItems.Select(i => i.Id).SingleAsync();

        await f.Repo.AddContentAsync(new BulkScoringItemContent { ItemId = id, Kind = BulkContentKind.Converted, ContentBase64 = "AAAA", SizeBytes = 3, Sha256 = "1" });
        await f.Repo.AddContentAsync(new BulkScoringItemContent { ItemId = id, Kind = BulkContentKind.Converted, ContentBase64 = "BBBB", SizeBytes = 3, Sha256 = "2" });

        (await f.Repo.GetContentAsync(id, BulkContentKind.Converted))!.Sha256.Should().Be("2");
        await f.Repo.RemoveContentAsync(id, BulkContentKind.Converted);
        (await f.Repo.GetContentAsync(id, BulkContentKind.Converted)).Should().BeNull();
        await f.Repo.Invoking(r => r.AddContentAsync(new BulkScoringItemContent { ItemId = id, Kind = BulkContentKind.Original }))
            .Should().ThrowAsync<InvalidOperationException>("neither blob nor inline content");
    }

    [Fact]
    public async Task Drafts_expire_are_purged_and_never_listed_or_notified()
    {
        await using var f = await Fixture.CreateAsync();
        var kept = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(1, "k"), expires: Now.AddMinutes(10)));
        var gone = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(1, "g"), expires: Now.AddMinutes(-1)));
        var active = await f.ActiveAsync("act", 1, owningStack: "B");

        (await f.Repo.ListByJobAsync("job-1", 1, 10)).Items.Select(r => r.Id).Should().Equal(active.Id);
        (await f.Repo.PurgeExpiredDraftsAsync(Now)).Should().Equal(gone.Id);
        (await f.Repo.GetAsync(gone.Id)).Should().BeNull();
        (await f.Repo.GetAsync(kept.Id)).Should().NotBeNull();
        (await f.Db.BulkScoringItems.CountAsync()).Should().Be(2, "the purged draft's items cascade");
        (await f.Repo.ListUnacknowledgedAsync("actor")).Should().BeEmpty();
        (await f.Repo.DeleteDraftAsync(kept.Id, "someone-else")).Should().BeFalse();
        (await f.Repo.DeleteDraftAsync(kept.Id, "actor")).Should().BeTrue();
    }

    [Fact]
    public async Task Completed_requests_notify_the_owner_until_acknowledged()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 1, owningStack: "B");
        await f.Repo.RequestCancelAsync(request.Id, request.ConcurrencyVersion, "actor", Now);

        (await f.Repo.ListUnacknowledgedAsync("actor")).Should().ContainSingle();
        (await f.Repo.ListUnacknowledgedAsync("other")).Should().BeEmpty();
        await f.Repo.AcknowledgeAsync(request.Id, Now);
        (await f.Repo.ListUnacknowledgedAsync("actor")).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_job_removes_its_requests_items_content_and_authorizations()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.ActiveAsync("a", 2, owningStack: "B", withAuthorization: true);

        (await f.Repo.ListRequestIdsForJobAsync("job-1")).Should().Equal(request.Id);
        await f.Repo.DeleteForJobAsync("job-1");

        (await f.Db.BulkScoringRequests.CountAsync()).Should().Be(0);
        (await f.Db.BulkScoringItems.CountAsync()).Should().Be(0);
        (await f.Db.BulkRequestAuthorizations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Settings_default_when_absent_and_save_with_an_optimistic_version()
    {
        await using var f = await Fixture.CreateAsync();
        var repo = new BulkImportSettingsRepository(f.Db);

        (await repo.GetPersistedAsync()).Should().BeNull();
        var saved = await repo.SaveAsync(new BulkImportSettings { MaxFilesPerRequest = 1_500, UpdatedByActorId = "admin", UpdatedAt = Now }, 0);
        saved.ConcurrencyVersion.Should().Be(1);

        var again = await repo.SaveAsync(new BulkImportSettings { MaxFilesPerRequest = 2_000, UpdatedByActorId = "admin", UpdatedAt = Now }, 1);
        again.ConcurrencyVersion.Should().Be(2);
        await repo.Invoking(r => r.SaveAsync(new BulkImportSettings { MaxFilesPerRequest = 5, UpdatedByActorId = "admin" }, 1))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("stale_version");
        (await repo.GetPersistedAsync())!.MaxFilesPerRequest.Should().Be(2_000);
    }

    [Fact]
    public async Task Authorizations_are_listed_for_discard_when_their_request_is_finished_or_cancelled_and_idle()
    {
        await using var f = await Fixture.CreateAsync();
        var finished = await f.ActiveAsync("a", 1, owningStack: "B", withAuthorization: true);
        var running = await f.ActiveAsync("b", 1, owningStack: "B", withAuthorization: true);
        await f.Repo.RequestCancelAsync(finished.Id, finished.ConcurrencyVersion, "actor", Now);
        var repo = new BulkRequestAuthorizationRepository(f.Db);

        (await repo.ListDiscardableAsync()).Should().Equal(finished.Id);

        var record = (await repo.GetAsync(finished.Id))!;
        record.Discard(Now);
        await repo.SaveAsync(record);
        (await repo.ListDiscardableAsync()).Should().BeEmpty();
        (await repo.ListActiveObtainedBeforeAsync(Now.AddDays(1))).Should().Equal(running.Id);
        (await repo.ListActiveObtainedBeforeAsync(Now.AddDays(-1))).Should().BeEmpty();
    }

    internal static List<BulkScoringItem> Items(int count, string prefix) =>
        Enumerable.Range(0, count).Select(i => new BulkScoringItem
        {
            Ordinal = i,
            SourceDriveId = "drv",
            SourceDriveItemId = $"{prefix}{i}",
            SourceETag = "e",
            FileName = $"{prefix}{i}.pdf",
            SourcePath = "/",
            MimeType = "application/pdf",
            SizeBytes = 10,
        }).ToList();

    internal static BulkDiscoveryStepResult Step(
        IReadOnlyList<BulkScoringItem> items,
        Dictionary<BulkReasonCode, int>? tallies = null,
        int inaccessible = 0,
        int entries = 0,
        BulkDiscoveryStatus status = BulkDiscoveryStatus.MoreAvailable,
        DateTime? expires = null) =>
        new(items, tallies ?? [], inaccessible, entries, status, status == BulkDiscoveryStatus.MoreAvailable ? "{}" : null, expires ?? Now.AddMinutes(30));

    internal sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }
        public BulkScoringRepository Repo { get; }

        private Fixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
            Repo = new BulkScoringRepository(db, new FixedClock(Now));
        }

        public static async Task<Fixture> CreateAsync(
            bool migrate = true,
            Action<DbContextOptionsBuilder<AppDbContext>>? configure = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
            configure?.Invoke(options);
            var db = new AppDbContext(options.Options);
            await (migrate ? db.Database.MigrateAsync() : db.Database.EnsureCreatedAsync());
            db.Jobs.Add(new Job { Id = "job-1", Title = "Job" });
            await db.SaveChangesAsync();
            return new(connection, db);
        }

        public BulkScoringRequest NewRequest() => new()
        {
            JobId = "job-1",
            OwnerActorId = "actor",
            SourceKind = BulkSourceKind.SharePoint,
            SourceTenantId = "tenant",
            SourceAccountBindingHash = "account-binding",
            SourceDriveId = "drv",
            SourceFolderItemId = "fld",
            SourceFolderName = "Candidates",
            SourceFolderPath = "/Candidates",
            SourceWebUrl = "https://x",
            RasterDpi = 150,
            MaxFilesPerRequest = 30_000,
            MaxBytesPerFile = 8_388_608,
            MaxTotalBytes = 53_687_091_200,
            DiscoveryStepMaxFiles = 1_000,
            DiscoveryStepMaxEntries = 10_000,
            DiscoveryStepMaxSeconds = 45,
        };

        public async Task<BulkScoringRequest> ActiveAsync(string prefix, int count, string? owningStack, bool cross = false, bool withAuthorization = false)
        {
            var request = NewRequest();
            if (cross) request.SourceTenantId = "other-organizational-tenant";
            if (owningStack == "A") request.SourceAccountBindingHash = "other-account-binding";
            var draft = await Repo.CreateDraftAsync(request, Step(Items(count, prefix)));
            var auth = withAuthorization
                ? BulkRequestAuthorization.Establish(
                    draft.Id,
                    request.OwnerActorId,
                    request.SourceTenantId,
                    request.SourceAccountBindingHash,
                    request.SourceDriveId,
                    request.SourceFolderItemId,
                    [1],
                    [1],
                    "v1",
                    Now)
                : null;
            var active = await Repo.ConfirmAsync(draft.Id, draft.ConcurrencyVersion, false, false, null, auth, Now);
            Db.ChangeTracker.Clear();
            return active;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
