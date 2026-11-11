using FluentAssertions;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using Xunit;

namespace TalentMatch.Domain.Tests.BulkScoringRequests;

public sealed class BulkScoringStateMachineTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Wire_values_match_the_shared_contract_fixtures()
    {
        WireEnum.AllWireValues<BulkRequestStatus>().Should().Equal(BulkFixtures.Strings("requestStatuses"));
        WireEnum.AllWireValues<BulkRequestOutcome>().Should().Equal(BulkFixtures.Strings("requestOutcomes"));
        WireEnum.AllWireValues<BulkDiscoveryStatus>().Should().Equal(BulkFixtures.Strings("discoveryStatuses"));
        WireEnum.AllWireValues<BulkItemStatus>().Should().Equal(BulkFixtures.Strings("itemStatuses"));
        WireEnum.AllWireValues<BulkReasonCode>().Should().Equal(BulkFixtures.Strings("reasonCodes"));
        WireEnum.AllWireValues<BulkSourceKind>().Should().Equal(BulkFixtures.Strings("sourceKinds"));
        WireEnum.AllWireValues<BulkSourceConnectionStatus>().Should().Equal(BulkFixtures.Strings("sourceConnectionStatuses"));
        WireEnum.AllWireValues<BulkAuthorizationStatus>().Should().Equal(BulkFixtures.Strings("authorizationStatuses"));
        BulkExclusionCategories.All.Select(x => x.ToWire()).Should().Equal(BulkFixtures.Strings("exclusionCategories"));
    }

    [Fact]
    public void Item_transition_table_matches_the_fixture_for_every_pair()
    {
        var table = BulkFixtures.Table("itemTransitions");
        foreach (var from in Enum.GetValues<BulkItemStatus>())
            foreach (var to in Enum.GetValues<BulkItemStatus>())
                BulkStateMachine.CanMove(from, to).Should()
                    .Be(table[from.ToWire()].Contains(to.ToWire()), $"{from.ToWire()} -> {to.ToWire()}");
    }

    [Fact]
    public void Request_transition_table_matches_the_fixture_for_every_pair()
    {
        var table = BulkFixtures.Table("requestTransitions");
        foreach (var from in Enum.GetValues<BulkRequestStatus>())
            foreach (var to in Enum.GetValues<BulkRequestStatus>())
                BulkStateMachine.CanMove(from, to).Should()
                    .Be(table[from.ToWire()].Contains(to.ToWire()), $"{from.ToWire()} -> {to.ToWire()}");
    }

    [Fact]
    public void Authorization_transition_table_matches_the_fixture_for_every_pair()
    {
        var table = BulkFixtures.Table("authorizationTransitions");
        foreach (var from in Enum.GetValues<BulkAuthorizationStatus>())
            foreach (var to in Enum.GetValues<BulkAuthorizationStatus>())
                BulkStateMachine.CanMove(from, to).Should()
                    .Be(table[from.ToWire()].Contains(to.ToWire()), $"{from.ToWire()} -> {to.ToWire()}");
    }

    [Fact]
    public void Every_illegal_item_transition_throws_and_every_legal_one_applies()
    {
        foreach (var from in Enum.GetValues<BulkItemStatus>())
            foreach (var to in Enum.GetValues<BulkItemStatus>())
            {
                var item = new BulkScoringItem { Status = from };
                var act = () => item.TransitionTo(to, Now, BulkReasonCode.TransientExhausted);
                if (BulkStateMachine.CanMove(from, to))
                {
                    act.Should().NotThrow($"{from.ToWire()} -> {to.ToWire()}");
                    item.Status.Should().Be(to);
                }
                else
                {
                    act.Should().Throw<InvalidOperationException>($"{from.ToWire()} -> {to.ToWire()}");
                    item.Status.Should().Be(from);
                }
            }
    }

    [Fact]
    public void Failed_and_skipped_items_require_a_reason_and_cancelled_defaults_to_user_cancel()
    {
        new BulkScoringItem { Status = BulkItemStatus.Importing }
            .Invoking(i => i.TransitionTo(BulkItemStatus.Failed, Now)).Should().Throw<InvalidOperationException>();
        new BulkScoringItem { Status = BulkItemStatus.Importing }
            .Invoking(i => i.TransitionTo(BulkItemStatus.Skipped, Now)).Should().Throw<InvalidOperationException>();

        var cancelled = new BulkScoringItem { Status = BulkItemStatus.Waiting };
        cancelled.TransitionTo(BulkItemStatus.Cancelled, Now);
        cancelled.ReasonCode.Should().Be(BulkReasonCode.CancelledByUser);
        cancelled.TerminalAt.Should().Be(Now);
    }

    [Fact]
    public void Returning_to_waiting_and_terminal_states_release_the_lease()
    {
        var item = new BulkScoringItem { Status = BulkItemStatus.Importing, LeaseOwner = "w1", LeaseUntil = Now.AddMinutes(1) };
        item.TransitionTo(BulkItemStatus.Waiting, Now);
        item.LeaseOwner.Should().BeNull();
        item.LeaseUntil.Should().BeNull();
    }

    [Fact]
    public void Application_link_is_write_once_and_idempotent_for_the_same_ids()
    {
        var item = new BulkScoringItem();
        item.LinkApplication("app-1", "doc-1", Now);
        item.LinkApplication("app-1", "doc-1", Now.AddMinutes(1));
        item.ApplicationCreatedAt.Should().Be(Now);
        item.Invoking(i => i.LinkApplication("app-2", "doc-2", Now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Handing_off_to_scoring_records_the_hand_off_once()
    {
        var item = new BulkScoringItem { Status = BulkItemStatus.Importing };
        item.TransitionTo(BulkItemStatus.Scoring, Now);
        item.HandedOffAt.Should().Be(Now);
    }

    [Fact]
    public void Retryable_reason_table_matches_the_fixture()
    {
        var retryable = BulkFixtures.Strings("retryableReasons");
        var notRetryable = BulkFixtures.Strings("notRetryableReasons");
        foreach (var reason in Enum.GetValues<BulkReasonCode>())
        {
            var wire = reason.ToWire();
            BulkStateMachine.IsRetryable(reason).Should().Be(retryable.Contains(wire), wire);
            (retryable.Contains(wire) ^ notRetryable.Contains(wire)).Should().BeTrue($"{wire} must be in exactly one list");
        }
        BulkStateMachine.RetryRequiresResolution(BulkReasonCode.SourceMissing).Should().BeTrue();
        BulkStateMachine.RetryRequiresResolution(BulkReasonCode.AccessDenied).Should().BeTrue();
        BulkStateMachine.RetryRequiresResolution(BulkReasonCode.AuthorizationLapsed).Should().BeFalse();
    }

    [Fact]
    public void Items_handed_off_to_scoring_are_never_retryable()
    {
        BulkStateMachine.IsRetryableItem(BulkItemStatus.Failed, BulkReasonCode.ScoringFailed, Now).Should().BeFalse();
        BulkStateMachine.IsRetryableItem(BulkItemStatus.Failed, BulkReasonCode.TransientExhausted, Now).Should().BeFalse();
        BulkStateMachine.IsRetryableItem(BulkItemStatus.Failed, BulkReasonCode.TransientExhausted, null).Should().BeTrue();
        BulkStateMachine.IsRetryableItem(BulkItemStatus.Cancelled, BulkReasonCode.CancelledByUser, null).Should().BeTrue();
        BulkStateMachine.IsRetryableItem(BulkItemStatus.Completed, null, null).Should().BeFalse();
    }

    [Fact]
    public void Options_are_immutable_once_the_request_is_active()
    {
        var request = DraftWithItems(1);
        request.Confirm(convertToRasterPdf: true, allowDuplicates: false, "cfg-1", Now);
        request.Status.Should().Be(BulkRequestStatus.Active);
        request.ConvertToRasterPdf.Should().BeTrue();
        request.JobConfigVersionId.Should().Be("cfg-1");
        request.Invoking(r => r.SetOptions(false, true)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Confirm_requires_an_included_file_and_an_unexpired_draft()
    {
        new BulkScoringRequest { ExpiresAt = Now.AddMinutes(5) }
            .Invoking(r => r.Confirm(false, false, null, Now)).Should().Throw<InvalidOperationException>();

        var expired = DraftWithItems(1);
        expired.ExpiresAt = Now.AddSeconds(-1);
        expired.Invoking(r => r.Confirm(false, false, null, Now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Discover_more_is_allowed_only_on_an_unexpired_draft_that_has_more()
    {
        var draft = DraftWithItems(1);
        draft.Invoking(r => r.EnsureCanDiscoverMore(Now)).Should().NotThrow();

        draft.DiscoveryStatus = BulkDiscoveryStatus.Complete;
        draft.Invoking(r => r.EnsureCanDiscoverMore(Now)).Should().Throw<InvalidOperationException>();
        draft.DiscoveryStatus = BulkDiscoveryStatus.LimitReached;
        draft.Invoking(r => r.EnsureCanDiscoverMore(Now)).Should().Throw<InvalidOperationException>();

        var active = DraftWithItems(1);
        active.Confirm(false, false, null, Now);
        active.Invoking(r => r.EnsureCanDiscoverMore(Now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Outcome_is_set_only_when_the_request_completes()
    {
        var request = DraftWithItems(2);
        request.Outcome.Should().BeNull();
        request.Confirm(false, false, null, Now);
        request.Invoking(r => r.Complete(Now)).Should().Throw<InvalidOperationException>("items are still waiting");

        request.ApplyItemCounts([Item(BulkItemStatus.Completed), Item(BulkItemStatus.Completed)]);
        request.Complete(Now);
        request.Outcome.Should().Be(BulkRequestOutcome.AllSucceeded);
        request.TerminalAt.Should().Be(Now);
        request.Invoking(r => r.Complete(Now)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(2, 0, 0, false, "all_succeeded")]
    [InlineData(1, 1, 0, false, "partial")]
    [InlineData(0, 2, 0, false, "failed")]
    [InlineData(1, 0, 1, true, "cancelled")]
    [InlineData(0, 0, 0, false, "all_succeeded")]
    public void Outcome_is_computed_from_item_states(int completed, int failed, int cancelled, bool cancelRequested, string expected)
    {
        var items = Enumerable.Repeat(Item(BulkItemStatus.Completed), completed)
            .Concat(Enumerable.Repeat(Item(BulkItemStatus.Failed), failed))
            .Concat(Enumerable.Repeat(Item(BulkItemStatus.Cancelled), cancelled))
            .ToArray();
        var request = new BulkScoringRequest();
        request.ApplyItemCounts(items);
        if (cancelRequested)
        {
            request.Status = BulkRequestStatus.Active;
            request.RequestCancel("actor", Now);
        }
        request.ComputeOutcome().ToWire().Should().Be(expected);
    }

    [Fact]
    public void Skipped_duplicates_do_not_make_a_request_partial()
    {
        var request = new BulkScoringRequest();
        request.ApplyItemCounts([Item(BulkItemStatus.Completed), Item(BulkItemStatus.Skipped)]);
        request.ComputeOutcome().Should().Be(BulkRequestOutcome.AllSucceeded);
    }

    [Fact]
    public void Only_exclusion_categories_can_be_tallied_and_totals_add_up()
    {
        var request = new BulkScoringRequest();
        request.AddExcluded(BulkReasonCode.UnsupportedType, 3);
        request.AddExcluded(BulkReasonCode.UnsupportedType, 2);
        request.AddExcluded(BulkReasonCode.FileTooLarge, 1);
        request.ExcludedCount.Should().Be(6);
        request.GetExcludedByReason()[BulkReasonCode.UnsupportedType].Should().Be(5);
        request.Invoking(r => r.AddExcluded(BulkReasonCode.AccessDenied, 1)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Counts_are_recomputed_from_items_and_validated()
    {
        var request = new BulkScoringRequest();
        request.ApplyItemCounts([Item(BulkItemStatus.Waiting), Item(BulkItemStatus.Scoring), Item(BulkItemStatus.Skipped)]);
        request.WaitingCount.Should().Be(1);
        request.ScoringCount.Should().Be(1);
        request.SkippedCount.Should().Be(1);
        request.Invoking(r => r.ValidateInvariants()).Should().NotThrow();

        request.CompletedCount = 5;
        request.Invoking(r => r.ValidateInvariants()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_is_recorded_once_and_only_on_an_active_request()
    {
        var request = DraftWithItems(1);
        request.Invoking(r => r.RequestCancel("a", Now)).Should().Throw<InvalidOperationException>();
        request.Confirm(false, false, null, Now);
        request.RequestCancel("first", Now);
        request.RequestCancel("second", Now.AddMinutes(1));
        request.CancelledByActorId.Should().Be("first");
        request.CancelRequestedAt.Should().Be(Now);
    }

    [Fact]
    public void Content_requires_exactly_one_canonical_location()
    {
        new BulkScoringItemContent { ItemId = "i", BlobUri = "https://x/blob" }
            .Invoking(c => c.ValidateInvariants()).Should().NotThrow();
        new BulkScoringItemContent { ItemId = "i", ContentBase64 = "AAAA" }
            .Invoking(c => c.ValidateInvariants()).Should().NotThrow();
        new BulkScoringItemContent { ItemId = "i" }
            .Invoking(c => c.ValidateInvariants()).Should().Throw<InvalidOperationException>();
        new BulkScoringItemContent { ItemId = "i", BlobUri = "u", ContentBase64 = "AAAA" }
            .Invoking(c => c.ValidateInvariants()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Settings_validation_matches_the_fixture_and_defaults_apply_without_a_row()
    {
        foreach (var c in BulkFixtures.Contract.GetProperty("settingsValidation").EnumerateArray())
            BulkImportSettings.Validate(c.GetProperty("maxFilesPerRequest").GetInt64()).Count.Should()
                .Be(c.GetProperty("valid").GetBoolean() ? 0 : 1);

        BulkImportSettings.Defaults().MaxFilesPerRequest.Should()
            .Be(BulkFixtures.Contract.GetProperty("defaults").GetProperty("maxFilesPerRequest").GetInt32());
    }

    [Fact]
    public void Authorization_lapse_and_discard_clear_protected_material_and_never_reactivate()
    {
        var payload = new byte[] { 1, 2, 3 };
        var auth = BulkRequestAuthorization.Establish(
            "req", "actor", "tenant", "account", "drive", "folder", payload, [9, 9], "v1", Now);
        auth.IsActive.Should().BeTrue();
        auth.RecordUse(Now.AddMinutes(1));

        auth.MarkLapsed("token_unavailable", Now.AddHours(1));
        auth.Status.Should().Be(BulkAuthorizationStatus.Lapsed);
        auth.Payload.Should().BeNull();
        auth.WrappedDataKey.Should().BeNull();
        payload.Should().OnlyContain(b => b == 0, "the in-memory copy is zeroed");
        auth.Invoking(a => a.RecordUse(Now)).Should().Throw<InvalidOperationException>();
        auth.Invoking(a => a.MarkLapsed("x", Now)).Should().Throw<InvalidOperationException>();

        auth.Discard(Now.AddHours(2));
        auth.Status.Should().Be(BulkAuthorizationStatus.Discarded);
        auth.DiscardedAt.Should().Be(Now.AddHours(2));
        auth.Invoking(a => a.Discard(Now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_refreshed_session_replaces_the_protected_material_but_not_the_sign_in_time()
    {
        var oldPayload = new byte[] { 1, 2, 3 };
        var oldKey = new byte[] { 9, 9 };
        var auth = BulkRequestAuthorization.Establish(
            "req", "actor", "tenant", "account", "drive", "folder", oldPayload, oldKey, "v1", Now);

        auth.ReplaceProtectedMaterial([4, 5, 6], [8, 8], "v2", Now.AddHours(5));

        auth.Payload.Should().Equal(4, 5, 6);
        auth.WrappedDataKey.Should().Equal(8, 8);
        auth.KeyVersion.Should().Be("v2");
        auth.ObtainedAt.Should().Be(Now);
        auth.LastUsedAt.Should().Be(Now.AddHours(5));
        oldPayload.Should().OnlyContain(b => b == 0, "the replaced copy is zeroed");
        oldKey.Should().OnlyContain(b => b == 0);
        auth.Invoking(a => a.ReplaceProtectedMaterial([], [1], "v3", Now)).Should().Throw<ArgumentException>();

        auth.MarkLapsed("session_revoked", Now.AddHours(6));
        auth.Invoking(a => a.ReplaceProtectedMaterial([1], [1], "v3", Now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Authorization_age_limit_is_enforced()
    {
        var auth = BulkRequestAuthorization.Establish(
            "req", "actor", "tenant", "account", "drive", "folder", [1], [1], "v1", Now);
        auth.IsOlderThan(TimeSpan.FromHours(72), Now.AddHours(72)).Should().BeFalse();
        auth.IsOlderThan(TimeSpan.FromHours(72), Now.AddHours(73)).Should().BeTrue();
    }

    private static BulkScoringRequest DraftWithItems(int count)
    {
        var request = new BulkScoringRequest { ExpiresAt = Now.AddMinutes(30) };
        request.ApplyItemCounts(Enumerable.Range(0, count).Select(_ => Item(BulkItemStatus.Waiting)));
        return request;
    }

    private static BulkScoringItem Item(BulkItemStatus status) => new() { Status = status, SizeBytes = 10 };
}
