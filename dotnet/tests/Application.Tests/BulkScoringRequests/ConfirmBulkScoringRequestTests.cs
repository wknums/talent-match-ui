using System.Diagnostics;
using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class ConfirmBulkScoringRequestTests
{
    private static readonly DateTime AcceptedAt =
        new(2026, 10, 14, 11, 25, 44, DateTimeKind.Utc);

    [Fact]
    public async Task Confirmation_rechecks_current_product_and_job_access_before_source_or_persistence()
    {
        var harness = new ConfirmationHarness();
        harness.Jobs.Job = null;

        var error = (await FluentActions
            .Awaiting(() => harness.ConfirmAsync())
            .Should().ThrowAsync<BulkApiException>()).Which;

        (error.StatusCode, error.Code).Should().Be((404, "not_found"));
        harness.Source.ResolveCalls.Should().Be(0);
        harness.Repository.ConfirmCalls.Should().Be(0);
        harness.AuthorizationStore.EstablishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Confirmation_requires_fresh_access_to_the_exact_discovered_folder()
    {
        var harness = new ConfirmationHarness();
        harness.Source.ResolvedFolder = harness.Source.ResolvedFolder with
        {
            DriveId = "other-drive",
            ItemId = "other-folder",
        };

        await FluentActions
            .Awaiting(() => harness.ConfirmAsync())
            .Should().ThrowAsync<BulkApiException>(
                "fresh access to a different drive/folder must not authorize the discovered draft");

        harness.Source.ResolveCalls.Should().Be(1);
        harness.Repository.ConfirmCalls.Should().Be(0);
        harness.AuthorizationStore.EstablishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Confirmation_freezes_options_and_current_config_and_makes_the_request_and_items_immediately_durable()
    {
        var harness = new ConfirmationHarness();
        var originalItemIds = harness.Repository.Items.Select(item => item.Id).ToArray();
        var originalLimits = BulkScoringAccess.FrozenLimits(harness.Repository.Request);

        var result = await harness.ConfirmAsync(convertToRasterPdf: true, allowDuplicates: true);

        result.Created.Should().BeTrue();
        result.Request.Status.Should().Be("active");
        harness.Repository.Request.ConvertToRasterPdf.Should().BeTrue();
        harness.Repository.Request.AllowDuplicates.Should().BeTrue();
        harness.Repository.Request.CapturedScoringConfigVersionId.Should().Be("config-at-confirm");
        BulkScoringAccess.FrozenLimits(harness.Repository.Request).Should().Be(originalLimits);
        harness.Repository.CommitVisible.Should().BeTrue("acknowledgment follows the durable commit");

        var durable = await harness.Repository.GetAsync(result.Request.RequestId);
        var durableItems = await harness.Repository.GetItemsAsync(
            result.Request.RequestId,
            new BulkItemQuery(1, 100));

        durable.Should().BeSameAs(harness.Repository.Request);
        durableItems.Items.Select(item => item.Id).Should().Equal(originalItemIds);
        durableItems.Items.Should().OnlyContain(item => item.Status == BulkItemStatus.Waiting);

        harness.Jobs.Job!.CurrentConfigVersionId = "config-after-confirm";
        harness.UploadSettings.Value = new UploadSettings { MaxIndividualFileBytes = 1_048_576 };
        harness.BulkSettings.Value = new BulkImportSettings { MaxFilesPerRequest = 1 };

        var queried = await harness.QueryAsync(result.Request.RequestId);
        queried.JobConfigVersionId.Should().Be("config-at-confirm");
        queried.ConfigChangedSinceAcceptance.Should().BeTrue();
        queried.ConvertToRasterPdf.Should().BeTrue();
        queried.AllowDuplicates.Should().BeTrue();
        queried.Limits.Should().Be(result.Request.Limits);
    }

    [Fact]
    public async Task Confirmation_transfers_the_protected_cache_to_the_request_and_consumes_it_once()
    {
        var harness = new ConfirmationHarness();
        var originalPayload = harness.AuthorizationStore.PendingPayload.ToArray();
        var originalWrappedKey = harness.AuthorizationStore.PendingWrappedKey.ToArray();

        var first = await harness.ConfirmAsync();
        var second = await harness.ConfirmAsync();

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse();
        harness.AuthorizationStore.EstablishCalls.Should().Be(1);
        harness.AuthorizationStore.Consumed.Should().BeTrue();
        harness.AuthorizationStore.PendingPayload.Should().OnlyContain(value => value == 0);
        harness.AuthorizationStore.PendingWrappedKey.Should().OnlyContain(value => value == 0);

        var authorization = await harness.Authorizations.GetAsync(first.Request.RequestId);
        authorization.Should().NotBeNull();
        authorization!.IsBoundTo(
            first.Request.RequestId,
            ConfirmationHarness.ActorId,
            ConfirmationHarness.SourceTenantId,
            ConfirmationHarness.AccountBindingHash,
            ConfirmationHarness.DriveId,
            ConfirmationHarness.FolderId).Should().BeTrue();
        authorization.Payload.Should().Equal(originalPayload);
        authorization.WrappedDataKey.Should().Equal(originalWrappedKey);

        harness.Source.Contexts.Should().ContainSingle();
        harness.Source.Contexts.Single().ConnectionId.Should().Be(
            ConfirmationHarness.ConnectionId,
            "the fresh exact-folder read must use the verified pending connection being consumed");
    }

    [Fact]
    public async Task Confirmation_persists_the_submit_actor_and_utc_time_and_audits_the_same_facts()
    {
        var harness = new ConfirmationHarness();

        var result = await harness.ConfirmAsync();

        harness.Repository.Request.OwnerActorId.Should().Be(ConfirmationHarness.ActorId);
        harness.Repository.Request.ConfirmedAt.Should().Be(AcceptedAt);
        harness.Repository.Request.ConfirmedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        result.Request.SubmittedBy.Should().Be(ConfirmationHarness.ActorId);
        result.Request.SubmittedAt.Should().Be(
            AcceptedAt,
            "the public submitted time is the persisted confirmation time, not draft creation time");

        harness.Events.Saved.Should().ContainSingle(evt =>
            evt.EventType == "bulk-scoring-request.accepted"
            && evt.Actor == ConfirmationHarness.ActorId
            && evt.Timestamp == AcceptedAt
            && evt.CorrelationId == ConfirmationHarness.CorrelationId);
    }

    [Fact]
    public async Task Confirmation_rolls_back_the_cache_transfer_when_the_request_commit_fails()
    {
        var harness = new ConfirmationHarness();
        var itemIds = harness.Repository.Items.Select(item => item.Id).ToArray();
        harness.Repository.ThrowOnConfirm = true;

        await FluentActions
            .Awaiting(() => harness.ConfirmAsync())
            .Should().ThrowAsync<BulkApiException>();

        harness.Repository.Request.Status.Should().Be(BulkRequestStatus.Draft);
        harness.Repository.Items.Select(item => item.Id).Should().Equal(itemIds);
        harness.Repository.CommitVisible.Should().BeFalse();
        harness.AuthorizationStore.Consumed.Should().BeFalse(
            "the pending connection and request activation belong to one transaction");
        (await harness.Authorizations.GetAsync(ConfirmationHarness.RequestId)).Should().BeNull();
        harness.Events.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmation_rolls_back_request_items_authorization_and_connection_when_submit_audit_fails()
    {
        var harness = new ConfirmationHarness();
        var itemIds = harness.Repository.Items.Select(item => item.Id).ToArray();
        harness.Events.ThrowOnAdd = true;

        await FluentActions
            .Awaiting(() => harness.ConfirmAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("audit_write_failed");

        harness.Repository.Request.Status.Should().Be(BulkRequestStatus.Draft);
        harness.Repository.Items.Select(item => item.Id).Should().Equal(itemIds);
        harness.Repository.CommitVisible.Should().BeFalse();
        harness.AuthorizationStore.Consumed.Should().BeFalse();
        (await harness.Authorizations.GetAsync(ConfirmationHarness.RequestId)).Should().BeNull();
        harness.Events.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task Sc002_acknowledgment_and_first_authorized_query_share_one_monotonic_sixty_second_stopwatch()
    {
        var harness = new ConfirmationHarness();
        var stopwatch = Stopwatch.StartNew();

        var acknowledgment = await harness.ConfirmAsync();
        var firstAuthorizedQuery = await harness.QueryAsync(acknowledgment.Request.RequestId);
        stopwatch.Stop();

        acknowledgment.Created.Should().BeTrue();
        firstAuthorizedQuery.RequestId.Should().Be(acknowledgment.Request.RequestId);
        firstAuthorizedQuery.Status.Should().Be("active");
        harness.Repository.CommitVisible.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThanOrEqualTo(
            TimeSpan.FromSeconds(60),
            "SC-002 measures confirmation acknowledgment and the first successful authorized query on one monotonic stopwatch");
    }

    private sealed class ConfirmationHarness
    {
        public const string ActorId = "11111111-1111-4111-8111-111111111111";
        public const string JobId = "job-1";
        public const string RequestId = "request-1";
        public const string ConnectionId = "connection-1";
        public const string SourceTenantId = "source-tenant";
        public const string AccountBindingHash = "account-binding";
        public const string DriveId = "drive-1";
        public const string FolderId = "folder-1";
        public const string CorrelationId = "correlation-1";

        public FixedTimeProvider Clock { get; } = new(AcceptedAt);
        public FakeJobRepository Jobs { get; } = new();
        public FakeSharePointSource Source { get; } = new();
        public FakeRequestAuthorizationRepository Authorizations { get; } = new();
        public FakeUploadSettingsRepository UploadSettings { get; } = new();
        public FakeBulkImportSettingsRepository BulkSettings { get; } = new();
        public FakeProcessingEventRepository Events { get; } = new();
        public FakeRequestAuthorizationStore AuthorizationStore { get; }
        public FakeBulkScoringRepository Repository { get; }
        public BulkScoringAccess Access { get; }

        public ConfirmationHarness()
        {
            AuthorizationStore = new FakeRequestAuthorizationStore(Clock);
            Repository = new FakeBulkScoringRepository(CreateDraft(), CreateItems(), Authorizations);
            Access = new BulkScoringAccess(
                BulkScoringOptions.Create(_ => null, isProduction: false, isDevelopment: true),
                Jobs,
                new FakeCurrentUserService(),
                UploadSettings,
                BulkSettings,
                Source,
                Authorizations);
        }

        public Task<BulkConfirmResult> ConfirmAsync(bool convertToRasterPdf = false, bool allowDuplicates = false) =>
            new ConfirmBulkScoringRequestHandler(
                    Access,
                    Repository,
                    Events,
                    Jobs,
                    Clock,
                    AuthorizationStore)
                .Handle(
                    new ConfirmBulkScoringRequestCommand(
                        RequestId,
                        new BulkConfirmRequest(convertToRasterPdf, allowDuplicates),
                        CorrelationId),
                    CancellationToken.None);

        public Task<BulkRequestDetailDto> QueryAsync(string requestId) =>
            new GetBulkScoringRequestHandler(Access, Repository, Jobs)
                .Handle(new GetBulkScoringRequestQuery(requestId), CancellationToken.None);

        private static BulkScoringRequest CreateDraft() => new()
        {
            Id = RequestId,
            JobId = JobId,
            OwnerActorId = ActorId,
            SourceKind = BulkSourceKind.SharePoint,
            SourceTenantId = SourceTenantId,
            SourceAccountBindingHash = AccountBindingHash,
            SourceDriveId = DriveId,
            SourceFolderItemId = FolderId,
            SourceFolderName = "Candidates",
            SourceFolderPath = "/Candidates",
            SourceWebUrl = "https://contoso.sharepoint.com/sites/hr/Shared Documents/Candidates",
            ConnectionId = ConnectionId,
            RasterDpi = 150,
            MaxFilesPerRequest = 30_000,
            MaxBytesPerFile = 8_388_608,
            MaxTotalBytes = 53_687_091_200,
            DiscoveryStepMaxFiles = 1_000,
            DiscoveryStepMaxEntries = 10_000,
            DiscoveryStepMaxSeconds = 45,
            DiscoveryStatus = BulkDiscoveryStatus.Complete,
            IncludedItemCount = 2,
            IncludedBytes = 2_003,
            ExpiresAt = AcceptedAt.AddMinutes(20),
            CreatedAt = AcceptedAt.AddMinutes(-10),
            ConcurrencyVersion = 7,
        };

        private static List<BulkScoringItem> CreateItems() =>
        [
            new()
            {
                Id = "item-1",
                RequestId = RequestId,
                Ordinal = 0,
                SourceDriveId = DriveId,
                SourceDriveItemId = "source-1",
                SourceETag = "etag-1",
                FileName = "one.pdf",
                SourcePath = "/Candidates/one.pdf",
                MimeType = "application/pdf",
                SizeBytes = 1_001,
                Status = BulkItemStatus.Waiting,
                CreatedAt = AcceptedAt.AddMinutes(-10),
                UpdatedAt = AcceptedAt.AddMinutes(-10),
            },
            new()
            {
                Id = "item-2",
                RequestId = RequestId,
                Ordinal = 1,
                SourceDriveId = DriveId,
                SourceDriveItemId = "source-2",
                SourceETag = "etag-2",
                FileName = "two.docx",
                SourcePath = "/Candidates/two.docx",
                MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                SizeBytes = 1_002,
                Status = BulkItemStatus.Waiting,
                CreatedAt = AcceptedAt.AddMinutes(-10),
                UpdatedAt = AcceptedAt.AddMinutes(-10),
            },
        ];
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = new(utcNow);
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FakeCurrentUserService : ICurrentUserService
    {
        public string? UserId => ConfirmationHarness.ActorId;
        public string? Username => "recruiter@example.com";
        public string? Role => "admin";
        public string? Department => "HR";
        public bool IsAdmin => true;
        public Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CurrentAuthorizationState?>(null);
    }

    private sealed class FakeJobRepository : IJobRepository
    {
        public Job? Job { get; set; } = new()
        {
            Id = ConfirmationHarness.JobId,
            Title = "Platform Engineer",
            Status = "active",
            CurrentConfigVersionId = "config-at-confirm",
        };

        public int GetByIdCalls { get; private set; }

        public Task<Job?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            GetByIdCalls++;
            return Task.FromResult(Job is not null && Job.Id == id ? Job : null);
        }

        public Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<Job>>();
        public Task<IReadOnlyList<Job>> GetByDepartmentAsync(string department, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<Job>>();
        public Task<IReadOnlyList<Job>> GetByDepartmentsOrCreatorAsync(IEnumerable<string> departments, string creatorId, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<Job>>();
        public Task<Job?> GetByIdWithoutApplicationsAsync(string id, CancellationToken cancellationToken = default) =>
            GetByIdAsync(id, cancellationToken);
        public Task AddAsync(Job job, CancellationToken cancellationToken = default) => Unexpected();
        public Task UpdateAsync(Job job, CancellationToken cancellationToken = default) => Unexpected();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Unexpected();
        public Task AddConfigVersionAsync(JobConfigVersion version, CancellationToken cancellationToken = default) => Unexpected();
        public Task<IReadOnlyList<JobConfigVersion>> GetConfigVersionsAsync(string jobId, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<JobConfigVersion>>();
    }

    private sealed class FakeSharePointSource : ISharePointSource
    {
        public SourceFolder ResolvedFolder { get; set; } = new(
            BulkSourceKind.SharePoint,
            ConfirmationHarness.DriveId,
            ConfirmationHarness.FolderId,
            "Candidates",
            "/Candidates",
            "https://contoso.sharepoint.com/sites/hr/Shared Documents/Candidates",
            ConfirmationHarness.SourceTenantId);

        public int ResolveCalls { get; private set; }
        public List<SourceCallContext> Contexts { get; } = [];

        public Task<SourceFolder> ResolveFolderAsync(
            string folderUrl,
            SourceCallContext context,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            Contexts.Add(context);
            return Task.FromResult(ResolvedFolder);
        }

        public bool CanConvertToPdf(string extension) => extension is ".docx" or ".md";
        public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default) => Unexpected<SourcePage>();
        public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<string>>();
        public Task<SourceItemMetadata?> GetItemMetadataAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => Unexpected<SourceItemMetadata?>();
        public Task<byte[]> DownloadAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => Unexpected<byte[]>();
        public Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => Unexpected<byte[]>();
    }

    private sealed class FakeUploadSettingsRepository : IUploadSettingsRepository
    {
        public UploadSettings? Value { get; set; }
        public Task<UploadSettings?> GetPersistedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task<UploadSettings> SaveAsync(UploadSettings settings, int expectedConcurrencyVersion, CancellationToken cancellationToken = default) => Unexpected<UploadSettings>();
    }

    private sealed class FakeBulkImportSettingsRepository : IBulkImportSettingsRepository
    {
        public BulkImportSettings? Value { get; set; }
        public Task<BulkImportSettings?> GetPersistedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task<BulkImportSettings> SaveAsync(BulkImportSettings settings, int expectedConcurrencyVersion, CancellationToken cancellationToken = default) => Unexpected<BulkImportSettings>();
    }

    private sealed class FakeRequestAuthorizationRepository : IBulkRequestAuthorizationRepository
    {
        private readonly Dictionary<string, BulkRequestAuthorization> _saved = new(StringComparer.Ordinal);

        public Task<BulkRequestAuthorization?> GetAsync(string requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_saved.GetValueOrDefault(requestId));

        public Task SaveAsync(BulkRequestAuthorization authorization, CancellationToken cancellationToken = default)
        {
            _saved.Add(authorization.RequestId, authorization);
            return Task.CompletedTask;
        }

        public void Remove(string requestId) => _saved.Remove(requestId);
        public Task<IReadOnlyList<string>> ListActiveObtainedBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<string>>();
        public Task<IReadOnlyList<string>> ListDiscardableAsync(CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<string>>();
    }

    private sealed class FakeRequestAuthorizationStore(FixedTimeProvider clock) : IRequestAuthorizationStore
    {
        public byte[] PendingPayload { get; } = [10, 20, 30, 40];
        public byte[] PendingWrappedKey { get; } = [50, 60, 70, 80];
        public int EstablishCalls { get; private set; }
        public bool Consumed { get; private set; }

        public Task<BulkRequestAuthorization> TransferToRequestAsync(
            string requestId,
            string connectionId,
            string recruiterActorId,
            string jobId,
            CancellationToken cancellationToken = default)
        {
            EstablishCalls++;
            if (Consumed) throw new InvalidOperationException("source_connection_already_consumed");

            var authorization = BulkRequestAuthorization.Establish(
                requestId,
                recruiterActorId,
                ConfirmationHarness.SourceTenantId,
                ConfirmationHarness.AccountBindingHash,
                ConfirmationHarness.DriveId,
                ConfirmationHarness.FolderId,
                PendingPayload.ToArray(),
                PendingWrappedKey.ToArray(),
                "key-v1",
                clock.GetUtcNow().UtcDateTime);

            Array.Clear(PendingPayload);
            Array.Clear(PendingWrappedKey);
            Consumed = true;
            return Task.FromResult(authorization);
        }

        public Task RollbackTransferAsync(
            BulkRequestAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            authorization.Payload!.CopyTo(PendingPayload, 0);
            authorization.WrappedDataKey!.CopyTo(PendingWrappedKey, 0);
            Consumed = false;
            return Task.CompletedTask;
        }

        public Task<string> AcquireGraphTokenAsync(string requestId, CancellationToken cancellationToken = default) => Unexpected<string>();
        public Task MarkLapsedAsync(string requestId, string reason, CancellationToken cancellationToken = default) => Unexpected();
        public Task DiscardAsync(string requestId, CancellationToken cancellationToken = default) => Unexpected();
    }

    private sealed class FakeProcessingEventRepository : IProcessingEventRepository
    {
        public List<ProcessingEvent> Saved { get; } = [];
        public bool ThrowOnAdd { get; set; }

        public Task AddAsync(ProcessingEvent evt, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd) throw new InvalidOperationException("audit_write_failed");
            Saved.Add(evt);
            return Task.CompletedTask;
        }

        public Task AddAuthorizationEventAsync(
            string actor,
            string action,
            string subjectId,
            IReadOnlyDictionary<string, object?> safeDetails,
            string correlationId,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<IReadOnlyList<ProcessingEvent>> GetFilteredAsync(
            string? entityType = null,
            string? eventType = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            int page = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessingEvent>>(Saved);
    }

    private sealed class FakeBulkScoringRepository : IBulkScoringRepository
    {
        private readonly FakeRequestAuthorizationRepository _authorizations;

        public FakeBulkScoringRepository(
            BulkScoringRequest request,
            List<BulkScoringItem> items,
            FakeRequestAuthorizationRepository authorizations)
        {
            Request = request;
            Items = items;
            Request.Items = items;
            Request.ApplyItemCounts(items);
            Request.IncludedItemCount = items.Count;
            Request.IncludedBytes = items.Sum(item => item.SizeBytes);
            _authorizations = authorizations;
        }

        public BulkScoringRequest Request { get; }
        public List<BulkScoringItem> Items { get; }
        public int ConfirmCalls { get; private set; }
        public bool CommitVisible { get; private set; }
        public bool ThrowOnConfirm { get; set; }

        public async Task<BulkScoringRequest> ConfirmAsync(
            string requestId,
            int expectedConcurrencyVersion,
            bool convertToRasterPdf,
            bool allowDuplicates,
            string? jobConfigVersionId,
            BulkRequestAuthorization? authorization,
            DateTime utcNow,
            CancellationToken cancellationToken = default)
        {
            ConfirmCalls++;
            if (ThrowOnConfirm) throw new InvalidOperationException("commit_failed");
            if (requestId != Request.Id || expectedConcurrencyVersion != Request.ConcurrencyVersion)
                throw new InvalidOperationException("stale_version");

            Request.ApplyItemCounts(Items);
            Request.Confirm(convertToRasterPdf, allowDuplicates, jobConfigVersionId, utcNow);
            Request.ConcurrencyVersion++;
            if (authorization is not null)
            {
                Request.Authorization = authorization;
                await _authorizations.SaveAsync(authorization, cancellationToken);
            }
            CommitVisible = true;
            return Request;
        }

        public async Task<BulkScoringRequest> ConfirmAtomicallyAsync(
            string requestId,
            int expectedConcurrencyVersion,
            string submitActorId,
            bool convertToRasterPdf,
            bool allowDuplicates,
            string? jobConfigVersionId,
            DateTime utcNow,
            Func<CancellationToken, Task<BulkRequestAuthorization>> transferAuthorization,
            Func<BulkRequestAuthorization, CancellationToken, Task> rollbackAuthorization,
            IReadOnlyList<ProcessingEvent> auditEvents,
            Func<ProcessingEvent, CancellationToken, Task> persistAudit,
            CancellationToken cancellationToken = default)
        {
            BulkRequestAuthorization? authorization = null;
            var originalVersion = Request.ConcurrencyVersion;
            var originalOwner = Request.OwnerActorId;
            try
            {
                authorization = await transferAuthorization(cancellationToken);
                Request.OwnerActorId = submitActorId;
                BulkScoringRequest active;
                try
                {
                    active = await ConfirmAsync(
                        requestId,
                        expectedConcurrencyVersion,
                        convertToRasterPdf,
                        allowDuplicates,
                        jobConfigVersionId,
                        authorization,
                        utcNow,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new BulkConfirmationPersistenceException(ex);
                }
                foreach (var auditEvent in auditEvents)
                    await persistAudit(auditEvent, cancellationToken);
                return active;
            }
            catch
            {
                Request.Status = BulkRequestStatus.Draft;
                Request.OwnerActorId = originalOwner;
                Request.CapturedScoringConfigVersionId = null;
                Request.Authorization = null;
                Request.ConcurrencyVersion = originalVersion;
                CommitVisible = false;
                _authorizations.Remove(requestId);
                if (authorization is not null)
                    await rollbackAuthorization(authorization, CancellationToken.None);
                throw;
            }
        }

        public Task<BulkScoringRequest?> GetAsync(string requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult(requestId == Request.Id && (Request.Status == BulkRequestStatus.Draft || CommitVisible)
                ? Request
                : null);

        public Task<BulkPage<BulkScoringItem>> GetItemsAsync(
            string requestId,
            BulkItemQuery query,
            CancellationToken cancellationToken = default)
        {
            var selected = Items
                .Where(item => item.RequestId == requestId)
                .OrderBy(item => item.Ordinal)
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToArray();
            return Task.FromResult(new BulkPage<BulkScoringItem>(
                selected,
                query.Page,
                query.PageSize,
                Items.Count(item => item.RequestId == requestId)));
        }

        public Task<BulkInclusionTotals> GetInclusionTotalsAsync(string requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BulkInclusionTotals(
                Items.Count,
                Items.Sum(item => item.SizeBytes),
                Items.Count(item => !item.ExcludedWhenRasterized),
                Items.Where(item => !item.ExcludedWhenRasterized).Sum(item => item.SizeBytes)));

        public Task<BulkScoringRequest> CreateDraftAsync(BulkScoringRequest draft, BulkDiscoveryStepResult firstStep, CancellationToken cancellationToken = default) => Unexpected<BulkScoringRequest>();
        public Task<BulkScoringRequest> ApplyDiscoveryStepAsync(string requestId, int expectedConcurrencyVersion, BulkDiscoveryStepResult step, CancellationToken cancellationToken = default) => Unexpected<BulkScoringRequest>();
        public Task<BulkScoringRequest> CreateRetryRequestAsync(BulkScoringRequest retry, IReadOnlyList<BulkScoringItem> items, BulkRequestAuthorization? authorization, CancellationToken cancellationToken = default) => Unexpected<BulkScoringRequest>();
        public Task<bool> DeleteDraftAsync(string requestId, string ownerActorId, CancellationToken cancellationToken = default) => Unexpected<bool>();
        public Task<IReadOnlyList<string>> PurgeExpiredDraftsAsync(DateTime utcNow, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<string>>();
        public Task<BulkPage<BulkScoringRequest>> ListByJobAsync(string jobId, int page, int pageSize, CancellationToken cancellationToken = default) => Unexpected<BulkPage<BulkScoringRequest>>();
        public Task<IReadOnlyList<BulkScoringRequest>> ListUnacknowledgedAsync(string ownerActorId, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<BulkScoringRequest>>();
        public Task<BulkScoringItem?> GetItemAsync(string requestId, string itemId, CancellationToken cancellationToken = default) => Unexpected<BulkScoringItem?>();
        public Task<BulkScoringItemContent?> GetContentAsync(string itemId, BulkContentKind kind, CancellationToken cancellationToken = default) => Unexpected<BulkScoringItemContent?>();
        public Task<IReadOnlyList<BulkScoringItem>> GetRetryableItemsAsync(string requestId, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<BulkScoringItem>>();
        public Task<IReadOnlySet<string>> FindExistingSourceItemIdsAsync(string requestId, IReadOnlyCollection<string> sourceItemIds, CancellationToken cancellationToken = default) => Unexpected<IReadOnlySet<string>>();
        public Task<IReadOnlyList<BulkScoringItem>> ClaimDueItemsAsync(string leaseOwner, int maxItems, DateTime utcNow, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<BulkScoringItem>>();
        public Task<int> ReclaimExpiredLeasesAsync(DateTime utcNow, CancellationToken cancellationToken = default) => Unexpected<int>();
        public Task ReleaseLeasesAsync(string leaseOwner, CancellationToken cancellationToken = default) => Unexpected();
        public Task SaveItemAsync(BulkScoringItem item, CancellationToken cancellationToken = default) => Unexpected();
        public Task AddContentAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default) => Unexpected();
        public Task RemoveContentAsync(string itemId, BulkContentKind kind, CancellationToken cancellationToken = default) => Unexpected();
        public Task<bool> TryClaimFingerprintAsync(string itemId, string fingerprint, CancellationToken cancellationToken = default) => Unexpected<bool>();
        public Task<int> FailNonTerminalItemsAsync(string requestId, BulkReasonCode reason, string message, DateTime utcNow, CancellationToken cancellationToken = default) => Unexpected<int>();
        public Task<IReadOnlyList<BulkScoringItem>> GetScoringItemsBatchAsync(int maxItems, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<BulkScoringItem>>();
        public Task<BulkScoringRequest?> TryFinalizeAsync(string requestId, DateTime utcNow, CancellationToken cancellationToken = default) => Unexpected<BulkScoringRequest?>();
        public Task<IReadOnlyList<string>> ListActiveRequestIdsAsync(CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<string>>();
        public Task<BulkScoringRequest> RequestCancelAsync(string requestId, int expectedConcurrencyVersion, string actorId, DateTime utcNow, CancellationToken cancellationToken = default) => Unexpected<BulkScoringRequest>();
        public Task AcknowledgeAsync(string requestId, DateTime utcNow, CancellationToken cancellationToken = default) => Unexpected();
        public Task<IReadOnlyList<string>> ListRequestIdsForJobAsync(string jobId, CancellationToken cancellationToken = default) => Unexpected<IReadOnlyList<string>>();
        public Task DeleteForJobAsync(string jobId, CancellationToken cancellationToken = default) => Unexpected();
    }

    private static Task Unexpected() => Task.FromException(new InvalidOperationException("Unexpected fake call."));
    private static Task<T> Unexpected<T>() => Task.FromException<T>(new InvalidOperationException("Unexpected fake call."));
}
