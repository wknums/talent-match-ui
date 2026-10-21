using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class BulkAuthorizationCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_dedicated_cleanup_service_is_required_for_the_fifteen_minute_guarantee()
    {
        var service = typeof(BulkScoringCycle).Assembly.GetType(
            "TalentMatch.Application.BulkScoringRequests.Services.BulkAuthorizationCleanupService");

        service.Should().NotBeNull(
            "terminal and cancellation-settled authorization cleanup must have its own retryable service boundary");
    }

    [Fact]
    public async Task Terminal_and_cancellation_settled_authorizations_are_discarded_before_fifteen_minutes()
    {
        var terminal = CompletedRequest("terminal", Now.UtcDateTime.AddMinutes(-14).AddSeconds(-59));
        var cancelled = CompletedRequest("cancelled", Now.UtcDateTime.AddMinutes(-14).AddSeconds(-59), cancel: true);
        var repository = Repository();
        repository.Setup(r => r.GetAsync("terminal", It.IsAny<CancellationToken>())).ReturnsAsync(terminal);
        repository.Setup(r => r.GetAsync("cancelled", It.IsAny<CancellationToken>())).ReturnsAsync(cancelled);
        var authorizations = Authorizations();
        authorizations.Setup(r => r.ListDiscardableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["terminal", "cancelled"]);
        var store = new Mock<IRequestAuthorizationStore>();
        store.Setup(s => s.DiscardAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Cycle(repository, authorizations, store, new FixedClock(Now)).SweepAsync(default);

        terminal.TerminalAt.Should().BeAfter(Now.UtcDateTime.AddMinutes(-15));
        cancelled.TerminalAt.Should().BeAfter(Now.UtcDateTime.AddMinutes(-15));
        store.Verify(s => s.DiscardAsync("terminal", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.DiscardAsync("cancelled", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_expired_active_authorization_is_lapsed_with_a_safe_reason()
    {
        var repository = Repository();
        repository.Setup(r => r.FailNonTerminalItemsAsync(
                "expired", BulkReasonCode.AuthorizationLapsed, It.IsAny<string>(), Now.UtcDateTime,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var authorization = Authorization("expired");
        var authorizations = Authorizations();
        authorizations.Setup(r => r.GetAsync("expired", It.IsAny<CancellationToken>()))
            .ReturnsAsync(authorization);
        authorizations.Setup(r => r.ListActiveObtainedBeforeAsync(
                Now.UtcDateTime.AddHours(-1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["expired"]);
        var store = new Mock<IRequestAuthorizationStore>();
        store.Setup(s => s.MarkLapsedAsync("expired", "max_age_exceeded", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Cycle(
            repository,
            authorizations,
            store,
            new FixedClock(Now),
            new BulkScoringOptions { AuthorizationMaxAge = TimeSpan.FromHours(1) }).SweepAsync(default);

        store.Verify(s => s.MarkLapsedAsync(
            "expired", "max_age_exceeded", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Replayed_cleanup_is_an_idempotent_discard()
    {
        var repository = Repository();
        var authorizations = Authorizations();
        authorizations.Setup(r => r.ListDiscardableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["request-1"]);
        var discarded = false;
        var store = new Mock<IRequestAuthorizationStore>();
        store.Setup(s => s.DiscardAsync("request-1", It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (discarded) throw new InvalidOperationException("discard was applied twice");
                discarded = true;
            })
            .Returns(Task.CompletedTask);
        var cycle = Cycle(repository, authorizations, store, new FixedClock(Now));

        await cycle.SweepAsync(default);
        await FluentActions.Awaiting(() => cycle.SweepAsync(default))
            .Should().NotThrowAsync("cleanup retries must not repeat the discard transition or its audit event");
    }

    [Fact]
    public void Discard_removes_ciphertext_and_zeroes_the_original_buffers()
    {
        var payload = new byte[] { 10, 20, 30 };
        var wrappedKey = new byte[] { 40, 50, 60 };
        var authorization = BulkRequestAuthorization.Establish(
            "request-1", BulkHandlerKit.UserId, BulkHandlerKit.TenantId, "account-binding",
            "drive-id", "folder-id", payload, wrappedKey, "key-v1", Now.UtcDateTime.AddHours(-1));

        authorization.Discard(Now.UtcDateTime);

        authorization.Payload.Should().BeNull();
        authorization.WrappedDataKey.Should().BeNull();
        payload.Should().OnlyContain(value => value == 0);
        wrappedKey.Should().OnlyContain(value => value == 0);
        authorization.Status.Should().Be(BulkAuthorizationStatus.Discarded);
        authorization.DiscardedAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task Discard_audit_is_immutable_safe_and_survives_ciphertext_removal()
    {
        var authorization = Authorization("request-1");
        var persisted = new List<BulkAuditEvent>();
        var ledger = new Mock<IBulkAuditLedger>();
        ledger.Setup(l => l.AppendAsync(It.IsAny<IReadOnlyCollection<BulkAuditEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<BulkAuditEvent>, CancellationToken>((events, _) => persisted.AddRange(events))
            .Returns(Task.CompletedTask);
        var audit = BulkAuditWriter.Service(
            BulkAuditEventTypes.RequestAuthorizationDiscarded,
            "bulk-authorization-cleanup",
            BulkHandlerKit.UserId,
            Guid.NewGuid().ToString(),
            Now.UtcDateTime,
            "request_authorization",
            authorization.RequestId,
            new { state = "discarded", reasonCode = "request_terminal" });

        await new BulkAuditWriter(ledger: ledger.Object).AppendAsync([audit]);
        authorization.Discard(Now.UtcDateTime);

        persisted.Should().ContainSingle().Which.Should().BeSameAs(audit);
        audit.SafeFactsJson.Should().Contain("\"factVersion\":1").And.Contain("\"state\":\"discarded\"");
        audit.SafeFactsJson.Should().NotContainAny(
            "Candidates", "contoso.sharepoint.com", "actor@example.com", "payload", "wrappedDataKey");
        typeof(BulkAuditEvent).GetProperties()
            .Where(property => property.SetMethod is not null)
            .Should().OnlyContain(property =>
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)),
                "persisted audit facts must not expose mutable setters");
    }

    [Fact]
    public async Task A_lapse_notification_contains_only_safe_state_and_opaque_references()
    {
        var request = CompletedRequest("request-1", Now.UtcDateTime);
        request.SourceFolderName = "Candidates";
        request.SourceFolderPath = "/Recruiting/Candidates";
        request.SourceWebUrl = "https://contoso.sharepoint.com/sites/hr/Recruiting/Candidates";
        request.SourceAccountBindingHash = "source-account-identity";
        var authorization = Authorization(request.Id);
        authorization.MarkLapsed("interaction_required", Now.UtcDateTime);
        var authorizations = new Mock<IBulkRequestAuthorizationRepository>();
        authorizations.Setup(r => r.GetAsync(request.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(authorization);
        var repository = new Mock<IBulkScoringRepository>();
        repository.Setup(r => r.ListUnacknowledgedAsync(BulkHandlerKit.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([request]);
        var handler = new ListBulkScoringNotificationsHandler(
            BulkHandlerKit.Access(authorizations: authorizations.Object), repository.Object);

        var notices = await handler.Handle(new ListBulkScoringNotificationsQuery(), default);
        var serialized = JsonSerializer.Serialize(notices);

        notices.Should().ContainSingle().Which.AuthorizationState.Should().Be("lapsed");
        serialized.Should().Contain(request.Id);
        serialized.Should().NotContainAny(
            "Candidates",
            "/Recruiting",
            "contoso.sharepoint.com",
            "source-account-identity",
            "actor@example.com");
    }

    private static BulkScoringCycle Cycle(
        Mock<IBulkScoringRepository> repository,
        Mock<IBulkRequestAuthorizationRepository> authorizations,
        Mock<IRequestAuthorizationStore> store,
        TimeProvider clock,
        BulkScoringOptions? options = null) =>
        new(
            repository.Object,
            Mock.Of<IApplicationRepository>(),
            authorizations.Object,
            null!,
            options ?? new BulkScoringOptions(),
            clock,
            Mock.Of<ILogger<BulkScoringCycle>>(),
            store.Object);

    private static Mock<IBulkScoringRepository> Repository()
    {
        var repository = new Mock<IBulkScoringRepository>();
        repository.Setup(r => r.GetScoringItemsBatchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.ListActiveRequestIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.PurgeExpiredDraftsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return repository;
    }

    private static Mock<IBulkRequestAuthorizationRepository> Authorizations()
    {
        var authorizations = new Mock<IBulkRequestAuthorizationRepository>();
        authorizations.Setup(r => r.ListDiscardableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        authorizations.Setup(r => r.ListActiveObtainedBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return authorizations;
    }

    private static BulkRequestAuthorization Authorization(string requestId) =>
        BulkRequestAuthorization.Establish(
            requestId,
            BulkHandlerKit.UserId,
            BulkHandlerKit.TenantId,
            "account-binding",
            "drive-id",
            "folder-id",
            [1, 2, 3],
            [4, 5, 6],
            "key-v1",
            Now.UtcDateTime.AddHours(-2));

    private static BulkScoringRequest CompletedRequest(string id, DateTime completedAt, bool cancel = false)
    {
        var request = BulkHandlerKit.Request(BulkRequestStatus.Active, id);
        if (cancel) request.RequestCancel(BulkHandlerKit.UserId, completedAt.AddSeconds(-1));
        request.Complete(completedAt);
        return request;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
