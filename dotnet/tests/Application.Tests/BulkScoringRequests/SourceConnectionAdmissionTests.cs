using System.Net;
using FluentAssertions;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.BulkScoringRequests.SourceConnections;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class SourceConnectionAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Address = IPAddress.Parse("203.0.113.10");
    private const string FolderUrl = "https://contoso.sharepoint.com/sites/hr/Shared%20Documents/Candidates";

    [Fact]
    public async Task Product_actor_and_job_authorization_run_before_admission_or_external_work()
    {
        var admission = new RecordingAdmissionRepository();
        var workflow = new RecordingSourceConnectionWorkflow();
        var unauthenticated = Handler(null, admission, workflow);
        var missingJob = Handler("actor-1", admission, workflow);

        var unauthenticatedAct = () => unauthenticated.Handle(
            new BeginSourceConnectionCommand("job-1", new(FolderUrl)), CancellationToken.None);
        var missingJobAct = () => missingJob.Handle(
            new BeginSourceConnectionCommand("missing-job", new(FolderUrl)), CancellationToken.None);

        (await unauthenticatedAct.Should().ThrowAsync<BulkApiException>())
            .Which.Code.Should().Be("not_found");
        (await missingJobAct.Should().ThrowAsync<BulkApiException>())
            .Which.Code.Should().Be("not_found");
        workflow.PendingChecks.Should().Be(0);
        workflow.BeginCalls.Should().Be(0);
        admission.StartAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://onedrive.live.com/?id=personal")]
    public async Task Request_shape_is_rejected_before_pending_admission_or_external_work(string folderUrl)
    {
        var admission = new RecordingAdmissionRepository();
        var workflow = new RecordingSourceConnectionWorkflow();
        var handler = Handler("actor-1", admission, workflow);

        var act = () => handler.Handle(
            new BeginSourceConnectionCommand("job-1", new(folderUrl)), CancellationToken.None);

        (await act.Should().ThrowAsync<BulkApiException>())
            .Which.StatusCode.Should().Be(422);
        workflow.PendingChecks.Should().Be(0);
        workflow.BeginCalls.Should().Be(0);
        admission.StartAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Oversized_url_and_malformed_retry_id_are_rejected_before_admission()
    {
        var admission = new RecordingAdmissionRepository();
        var workflow = new RecordingSourceConnectionWorkflow();
        var handler = Handler("actor-1", admission, workflow);

        var oversized = () => handler.Handle(
            new BeginSourceConnectionCommand("job-1", new($"https://contoso.sharepoint.com/{new string('a', 4097)}")),
            CancellationToken.None);
        var malformedRetry = () => handler.Handle(
            new BeginSourceConnectionCommand("job-1", new(FolderUrl, "not-a-request-id")),
            CancellationToken.None);

        (await oversized.Should().ThrowAsync<BulkApiException>()).Which.Code.Should().Be("validation_failed");
        (await malformedRetry.Should().ThrowAsync<BulkApiException>()).Which.Code.Should().Be("validation_failed");
        workflow.PendingChecks.Should().Be(0);
        workflow.BeginCalls.Should().Be(0);
        admission.StartAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Ten_pending_connections_reject_with_safe_code_and_zero_external_work()
    {
        var admission = new RecordingAdmissionRepository();
        var workflow = new RecordingSourceConnectionWorkflow();
        workflow.PendingByActor["actor-1"] = 10;
        var handler = Handler("actor-1", admission, workflow);

        var act = () => handler.Handle(
            new BeginSourceConnectionCommand("job-1", new(FolderUrl)), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<BulkApiException>()).Which;
        error.StatusCode.Should().Be(429);
        error.Code.Should().Be("source_connection_limit_reached");
        workflow.BeginCalls.Should().Be(0);
        admission.StartAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Actor_is_limited_to_ten_starts_per_rolling_minute()
    {
        var admission = new RecordingAdmissionRepository();
        var workflow = new RecordingSourceConnectionWorkflow();
        var handler = Handler("actor-1", admission, workflow);

        for (var index = 0; index < 10; index++)
            await handler.Handle(new BeginSourceConnectionCommand("job-1", new(FolderUrl)), CancellationToken.None);

        var rejected = () => handler.Handle(
            new BeginSourceConnectionCommand("job-1", new(FolderUrl)), CancellationToken.None);

        var error = (await rejected.Should().ThrowAsync<BulkApiException>()).Which;
        error.StatusCode.Should().Be(429);
        error.Code.Should().Be("source_connection_rate_limited");
        workflow.BeginCalls.Should().Be(10);
    }

    [Fact]
    public async Task Source_ip_is_limited_to_thirty_starts_per_rolling_minute()
    {
        var admission = new RecordingAdmissionRepository();
        var workflow = new RecordingSourceConnectionWorkflow();

        for (var actor = 0; actor < 3; actor++)
        {
            var handler = Handler($"actor-{actor}", admission, workflow);
            for (var start = 0; start < 10; start++)
                await handler.Handle(new BeginSourceConnectionCommand("job-1", new(FolderUrl)), CancellationToken.None);
        }

        var rejectedHandler = Handler("actor-31", admission, workflow);
        var rejected = () => rejectedHandler.Handle(
            new BeginSourceConnectionCommand("job-1", new(FolderUrl)), CancellationToken.None);

        var error = (await rejected.Should().ThrowAsync<BulkApiException>()).Which;
        error.StatusCode.Should().Be(429);
        error.Code.Should().Be("source_connection_rate_limited");
        workflow.BeginCalls.Should().Be(30);
    }

    [Fact]
    public async Task Callback_admission_precedes_state_lookup_and_code_redemption()
    {
        var operations = new List<string>();
        var admission = new RecordingAdmissionRepository(operations);
        var workflow = new RecordingSourceConnectionWorkflow(operations);
        var handler = CallbackHandler(admission, workflow);

        await handler.Handle(
            new CompleteSourceConnectionCallbackCommand(new("state", "authorization-code")),
            CancellationToken.None);

        operations.Should().Equal("callback-admission", "state-lookup", "code-redemption");
    }

    [Fact]
    public async Task Source_ip_is_limited_to_sixty_callbacks_and_rejection_does_no_external_work()
    {
        var admission = new RecordingAdmissionRepository();
        var workflow = new RecordingSourceConnectionWorkflow();
        var handler = CallbackHandler(admission, workflow);

        for (var index = 0; index < 60; index++)
            await handler.Handle(
                new CompleteSourceConnectionCallbackCommand(new($"state-{index}", $"code-{index}")),
                CancellationToken.None);

        var rejected = () => handler.Handle(
            new CompleteSourceConnectionCallbackCommand(new("rejected-state", "rejected-code")),
            CancellationToken.None);

        var error = (await rejected.Should().ThrowAsync<BulkApiException>()).Which;
        error.StatusCode.Should().Be(429);
        error.Code.Should().Be("source_callback_rate_limited");
        workflow.StateLookups.Should().Be(60);
        workflow.CodeRedemptions.Should().Be(60);
    }

    private static BeginSourceConnectionHandler Handler(
        string? actorId,
        RecordingAdmissionRepository repository,
        RecordingSourceConnectionWorkflow workflow) =>
        new(
            Access(actorId),
            new SourceLinkClassifier(),
            Admission(repository),
            workflow,
            new FixedClientAddress(Address),
            new FixedTimeProvider(Now));

    private static CompleteSourceConnectionCallbackHandler CallbackHandler(
        RecordingAdmissionRepository repository,
        RecordingSourceConnectionWorkflow workflow) =>
        new(Admission(repository), workflow, new FixedClientAddress(Address));

    private static BulkSourceAdmissionService Admission(IBulkSourceAdmissionRepository repository) =>
        new(
            repository,
            new BulkScoringOptions { Mode = SharePointMode.Mock },
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            new FixedTimeProvider(Now));

    private static BulkScoringAccess Access(string? actorId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(user => user.UserId).Returns(actorId);
        currentUser.SetupGet(user => user.IsAdmin).Returns(actorId is not null);
        currentUser.Setup(user => user.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actorId is null
                ? null
                : new CurrentAuthorizationState(
                    new CurrentEntraClaims(
                        BulkHandlerKit.TenantId,
                        actorId,
                        $"{actorId}@example.com",
                        actorId,
                        $"{actorId}@example.com",
                        Now.AddMinutes(-1),
                        new HashSet<string>(),
                        new HashSet<string>()),
                    new User { Id = actorId, Username = $"{actorId}@example.com", FullName = actorId, IsActive = true },
                    [],
                    [new DepartmentMembership
                    {
                        UserId = actorId,
                        OrganizationId = BulkHandlerKit.OrganizationId,
                        DepartmentId = BulkHandlerKit.DepartmentId,
                        Status = "active",
                    }],
                    [new RoleAssignment
                    {
                        UserId = actorId,
                        TenantId = BulkHandlerKit.TenantId,
                        UserObjectId = actorId,
                        Role = "recruiter",
                        OrganizationId = BulkHandlerKit.OrganizationId,
                        DepartmentId = BulkHandlerKit.DepartmentId,
                        Source = "delegated",
                        Status = "active",
                    }],
                    [],
                    BulkHandlerKit.TenantId,
                    null));

        return new BulkScoringAccess(
            new BulkScoringOptions { Mode = SharePointMode.Mock },
            BulkHandlerKit.Jobs().Object,
            currentUser.Object,
            Mock.Of<IUploadSettingsRepository>(),
            Mock.Of<IBulkImportSettingsRepository>(),
            Mock.Of<ISharePointSource>(),
            Mock.Of<IBulkRequestAuthorizationRepository>());
    }

    private sealed class FixedClientAddress(IPAddress address) : IBulkSourceClientAddress
    {
        public IPAddress GetAddress() => address;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSourceConnectionWorkflow(List<string>? operations = null)
        : ISourceConnectionWorkflow
    {
        private readonly List<string> _operations = operations ?? [];

        public Dictionary<string, int> PendingByActor { get; } = new(StringComparer.Ordinal);
        public int PendingChecks { get; private set; }
        public int BeginCalls { get; private set; }
        public int StateLookups { get; private set; }
        public int CodeRedemptions { get; private set; }

        public Task<int> CountUnexpiredPendingAsync(
            string productActorId,
            DateTime utcNow,
            CancellationToken cancellationToken)
        {
            PendingChecks++;
            return Task.FromResult(PendingByActor.GetValueOrDefault(productActorId));
        }

        public Task<BeginSourceConnectionResponse> BeginAsync(
            SourceConnectionStart start,
            CancellationToken cancellationToken)
        {
            BeginCalls++;
            return Task.FromResult(new BeginSourceConnectionResponse(
                Guid.NewGuid().ToString(),
                "authorizing",
                new Uri("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize"),
                Now.AddMinutes(30).UtcDateTime));
        }

        public Task<SourceConnectionCallbackResult> CompleteCallbackAsync(
            SourceConnectionCallback callback,
            CancellationToken cancellationToken)
        {
            StateLookups++;
            _operations.Add("state-lookup");
            CodeRedemptions++;
            _operations.Add("code-redemption");
            return Task.FromResult(new SourceConnectionCallbackResult(new Uri("/jobs/job-1", UriKind.Relative)));
        }
    }

    private sealed class RecordingAdmissionRepository(List<string>? operations = null)
        : IBulkSourceAdmissionRepository
    {
        private readonly List<string> _operations = operations ?? [];
        private readonly List<(string Subject, DateTime At)> _actorStarts = [];
        private readonly List<(string Subject, DateTime At)> _ipStarts = [];
        private readonly List<(string Subject, DateTime At)> _callbacks = [];

        public int StartAttempts { get; private set; }

        public Task<bool> TryAdmitConnectionStartAsync(
            string actorSubjectHash,
            string ipSubjectHash,
            DateTime utcNow,
            int actorStartsPerMinute,
            int ipStartsPerMinute,
            CancellationToken cancellationToken = default)
        {
            StartAttempts++;
            var cutoff = utcNow.AddMinutes(-1);
            if (_actorStarts.Count(item => item.Subject == actorSubjectHash && item.At > cutoff) >= actorStartsPerMinute
                || _ipStarts.Count(item => item.Subject == ipSubjectHash && item.At > cutoff) >= ipStartsPerMinute)
                return Task.FromResult(false);

            _actorStarts.Add((actorSubjectHash, utcNow));
            _ipStarts.Add((ipSubjectHash, utcNow));
            return Task.FromResult(true);
        }

        public Task<bool> TryAdmitCallbackAsync(
            string ipSubjectHash,
            DateTime utcNow,
            int callbacksPerMinute,
            CancellationToken cancellationToken = default)
        {
            _operations.Add("callback-admission");
            var cutoff = utcNow.AddMinutes(-1);
            if (_callbacks.Count(item => item.Subject == ipSubjectHash && item.At > cutoff) >= callbacksPerMinute)
                return Task.FromResult(false);

            _callbacks.Add((ipSubjectHash, utcNow));
            return Task.FromResult(true);
        }

        public Task<int> CleanupExpiredAsync(DateTime utcNow, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
