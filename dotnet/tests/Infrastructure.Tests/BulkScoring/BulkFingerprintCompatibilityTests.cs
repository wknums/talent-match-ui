using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Common.Services;
using TalentMatch.Application.Uploads.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkFingerprintCompatibilityTests
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private static readonly FingerprintMatrix Matrix = LoadMatrix();
    private static readonly byte[] CrossPathBytes =
        File.ReadAllBytes(Path.Combine(FixtureRoot, "files", "multibyte.txt"));
    private static readonly string CrossPathFingerprint = ContentFingerprint.Compute(CrossPathBytes);

    [Fact]
    public void Canonical_fingerprint_is_lowercase_SHA256_of_the_exact_original_bytes()
    {
        foreach (var testCase in Matrix.Cases)
        {
            var bytes = ReadInput(testCase.Input);

            bytes.LongLength.Should().Be(testCase.SizeBytes, testCase.Name);
            ContentFingerprint.Compute(bytes).Should().Be(testCase.Expected, testCase.Name);
            testCase.Expected.Should().MatchRegex("^[0-9a-f]{64}$");
        }
    }

    [Fact]
    public void Compatible_pre_change_SQLite_and_SQL_Server_values_are_normalized_without_guessing()
    {
        foreach (var value in Matrix.CompatiblePreChangeProviderValues)
        {
            ContentFingerprint.NormalizeStored(value.Stored).Should().Be(
                value.Normalized,
                $"{value.Provider} records marked {value.Verdict} must retain that compatibility verdict");
        }
    }

    [Fact]
    public async Task Legacy_upload_recomputes_the_canonical_fingerprint_and_matches_pre_change_records()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(CrossPathFingerprint.ToUpperInvariant());

        var result = await fixture.LegacyUploadAsync(allowDuplicates: false, suppliedFingerprint: "client-value-is-not-authoritative");

        result.Should().BeEmpty();
        (await fixture.Context.ApplicationDocuments.AsNoTracking().SingleAsync())
            .Fingerprint.Should().Be(CrossPathFingerprint.ToUpperInvariant(), "compatible history is never rewritten");
    }

    [Fact]
    public async Task Optional_upload_uses_exact_original_bytes_and_matches_pre_change_records()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(CrossPathFingerprint.ToUpperInvariant());

        var item = await fixture.OptionalUploadAsync(allowDuplicates: false);

        item.Status.Should().Be(UploadItemStatus.SkippedDuplicate);
        item.ContentFingerprint.Should().Be(CrossPathFingerprint);
        (await fixture.Context.Applications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Bulk_path_uses_exact_original_bytes_and_emits_safe_atomic_duplicate_lifecycle_events()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(CrossPathFingerprint.ToUpperInvariant());

        var (outcome, item) = await fixture.BulkUploadAsync(allowDuplicates: false);

        outcome.Should().Be(BulkItemOutcome.Skipped);
        item.Fingerprint.Should().Be(CrossPathFingerprint);
        var events = await fixture.Context.BulkAuditEvents.AsNoTracking()
            .Where(e => e.SubjectId == item.Id)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();
        events.Select(e => e.EventType).Should().ContainInOrder(
            BulkAuditEventTypes.BulkItemFingerprintRecorded,
            BulkAuditEventTypes.BulkItemDuplicateClaimed,
            BulkAuditEventTypes.BulkItemDuplicateSkipped);
        events.Should().OnlyContain(e =>
            !e.SafeFactsJson.Contains(CrossPathFingerprint, StringComparison.OrdinalIgnoreCase)
            && !e.SafeFactsJson.Contains("multibyte", StringComparison.OrdinalIgnoreCase)
            && !e.SafeFactsJson.Contains(Convert.ToBase64String(CrossPathBytes), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Allow_duplicates_preserves_import_semantics_across_all_Stack_B_paths()
    {
        await using (var legacy = await Fixture.CreateAsync())
        {
            await legacy.AddExistingDocumentAsync(CrossPathFingerprint.ToUpperInvariant());
            (await legacy.LegacyUploadAsync(true, "untrusted")).Should().ContainSingle();
            (await legacy.Context.ApplicationDocuments.AsNoTracking()
                .SingleAsync(d => d.Fingerprint == CrossPathFingerprint))
                .Fingerprint.Should().Be(CrossPathFingerprint);
        }

        await using (var optional = await Fixture.CreateAsync())
        {
            await optional.AddExistingDocumentAsync(CrossPathFingerprint.ToUpperInvariant());
            (await optional.OptionalUploadAsync(true)).Status.Should().Be(UploadItemStatus.Succeeded);
            (await optional.Context.ApplicationDocuments.CountAsync()).Should().Be(2);
        }

        await using (var bulk = await Fixture.CreateAsync())
        {
            await bulk.AddExistingDocumentAsync(CrossPathFingerprint.ToUpperInvariant());
            var (outcome, item) = await bulk.BulkUploadAsync(true);
            outcome.Should().Be(BulkItemOutcome.HandedOff);
            item.Fingerprint.Should().Be(CrossPathFingerprint);
            (await bulk.Context.Applications.CountAsync()).Should().Be(2);
            var eventTypes = await bulk.Context.BulkAuditEvents.AsNoTracking()
                .Where(e => e.SubjectId == item.Id)
                .Select(e => e.EventType)
                .ToListAsync();
            eventTypes.Should().Contain(BulkAuditEventTypes.BulkItemFingerprintRecorded);
            eventTypes.Should().NotContain(BulkAuditEventTypes.BulkItemDuplicateClaimed);
            eventTypes.Should().NotContain(BulkAuditEventTypes.BulkItemDuplicateSkipped);
        }
    }

    private static byte[] ReadInput(FingerprintInput input) => input.Kind switch
    {
        "hex" => Convert.FromHexString(input.Value ?? string.Empty),
        "fixture" => File.ReadAllBytes(Path.Combine(FixtureRoot, input.Path!)),
        "repeat-byte" => Enumerable.Repeat((byte)input.Byte!.Value, input.Count!.Value).ToArray(),
        _ => throw new InvalidOperationException($"Unknown fingerprint fixture input '{input.Kind}'."),
    };

    private static FingerprintMatrix LoadMatrix() =>
        JsonSerializer.Deserialize<FingerprintMatrix>(
            File.ReadAllText(Path.Combine(FixtureRoot, "fingerprint-matrix.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("The fingerprint fixture matrix is invalid.");

    private static string FindFixtureRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "bulk-scoring-requests");
            if (File.Exists(Path.Combine(candidate, "fingerprint-matrix.json")))
                return candidate;
        }

        throw new DirectoryNotFoundException("tests/fixtures/bulk-scoring-requests was not found.");
    }

    private sealed record FingerprintMatrix(
        IReadOnlyList<FingerprintCase> Cases,
        IReadOnlyList<CompatibleValue> CompatiblePreChangeProviderValues);

    private sealed record FingerprintCase(
        string Name,
        FingerprintInput Input,
        long SizeBytes,
        string Expected);

    private sealed record FingerprintInput(
        string Kind,
        string? Value,
        string? Path,
        int? Byte,
        int? Count);

    private sealed record CompatibleValue(
        string Provider,
        string? Stored,
        string? Normalized,
        string Verdict);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ApplicationRepository _applications;
        private readonly BulkScoringRepository _bulkRepository;

        private Fixture(SqliteConnection connection, AppDbContext context)
        {
            _connection = connection;
            Context = context;
            _applications = new ApplicationRepository(context);
            _bulkRepository = new BulkScoringRepository(context, TimeProvider.System);
        }

        public AppDbContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            context.Jobs.Add(new Job { Id = "job-1", Title = "Fingerprint Job" });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return new Fixture(connection, context);
        }

        public async Task AddExistingDocumentAsync(string fingerprint)
        {
            var application = new TalentMatch.Domain.Entities.Application
            {
                JobId = "job-1",
                CandidateRef = $"existing-{Guid.NewGuid():N}",
                CandidateName = "Existing",
                Status = "Queued",
            };
            await _applications.AddAsync(application);
            await _applications.AddDocumentAsync(new ApplicationDocument
            {
                ApplicationId = application.Id,
                FileName = "historical.txt",
                FileType = "text/plain",
                FileSize = CrossPathBytes.Length,
                Fingerprint = fingerprint,
                ContentBase64 = Convert.ToBase64String(CrossPathBytes),
            });
            Context.ChangeTracker.Clear();
        }

        public Task<List<TalentMatch.Domain.Entities.Application>> LegacyUploadAsync(
            bool allowDuplicates,
            string suppliedFingerprint)
        {
            var handler = new UploadApplicationsCommandHandler(
                _applications,
                new JobRepository(Context));
            var file = new UploadedFile(
                "multibyte.txt",
                "text/plain",
                CrossPathBytes.Length,
                Convert.ToBase64String(CrossPathBytes),
                suppliedFingerprint);
            return handler.Handle(
                new UploadApplicationsCommand("job-1", [file], allowDuplicates),
                CancellationToken.None);
        }

        public async Task<UploadItem> OptionalUploadAsync(bool allowDuplicates)
        {
            var repository = new UploadSessionRepository(Context);
            var session = new UploadSession
            {
                Id = Guid.NewGuid().ToString(),
                JobId = "job-1",
                OwnerActorId = "owner-1",
                AllowDuplicates = allowDuplicates,
                FileConcurrency = UploadSettings.DefaultFileConcurrency,
                MaxIndividualFileBytes = 8_388_608,
                MaxInFlightBytes = UploadSettings.DefaultMaxInFlightBytes,
            };
            var occurrence = Guid.NewGuid();
            var item = new UploadItem
            {
                Id = Guid.NewGuid().ToString(),
                SessionId = session.Id,
                OccurrenceKey = occurrence.ToString(),
                FileName = "multibyte.txt",
                MimeType = "text/plain",
                RawSizeBytes = CrossPathBytes.Length,
            };
            await repository.CreateAsync(
                session,
                [item],
                new ProcessingEvent
                {
                    Actor = "owner-1",
                    EventType = "upload-session.created",
                    EntityType = nameof(UploadSession),
                    EntityId = session.Id,
                },
                0);

            var handler = new UploadItemContentCommandHandler(
                repository,
                _applications,
                new CurrentUser("owner-1"));
            await handler.Handle(
                new UploadItemContentCommand(
                    session.Id,
                    item.Id,
                    occurrence,
                    item.FileName,
                    item.MimeType,
                    CrossPathBytes,
                    Guid.NewGuid().ToString()),
                CancellationToken.None);
            Context.ChangeTracker.Clear();
            return (await repository.GetItemAsync(session.Id, item.Id, "owner-1"))!;
        }

        public async Task<(BulkItemOutcome Outcome, BulkScoringItem Item)> BulkUploadAsync(bool allowDuplicates)
        {
            var request = new BulkScoringRequest
            {
                Id = Guid.NewGuid().ToString(),
                JobId = "job-1",
                OwnerActorId = "owner-1",
                SourceKind = BulkSourceKind.SharePoint,
                SourceTenantId = "tenant",
                SourceAccountBindingHash = "binding",
                SourceDriveId = "drive",
                SourceFolderItemId = "folder",
                SourceFolderName = "Candidates",
                SourceFolderPath = "/Candidates",
                SourceWebUrl = "https://contoso.sharepoint.com/Candidates",
                CorrelationId = Guid.NewGuid().ToString(),
                MaxFilesPerRequest = 10,
                MaxBytesPerFile = 8_388_608,
                MaxTotalBytes = 64_000_000,
                DiscoveryStepMaxFiles = 1_000,
                DiscoveryStepMaxEntries = 10_000,
                DiscoveryStepMaxSeconds = 45,
                RasterDpi = 150,
                CapturedScoringConfigVersionId = "config-1",
            };
            request.SetOptions(convertToRasterPdf: false, allowDuplicates);
            request.Status = BulkRequestStatus.Active;
            var item = new BulkScoringItem
            {
                Id = Guid.NewGuid().ToString(),
                RequestId = request.Id,
                Ordinal = 0,
                SourceDriveId = "drive",
                SourceDriveItemId = Guid.NewGuid().ToString(),
                SourceETag = "etag",
                FileName = "multibyte.txt",
                SourcePath = "/",
                MimeType = "text/plain",
                SizeBytes = CrossPathBytes.Length,
                Status = BulkItemStatus.Importing,
            };
            Context.BulkScoringRequests.Add(request);
            Context.BulkScoringItems.Add(item);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            var processor = new BulkItemProcessor(
                _bulkRepository,
                _applications,
                new Source(CrossPathBytes),
                new InlineContentStore(),
                new NoRasterizer(),
                new BulkScoringOptions(),
                TimeProvider.System,
                NullLogger<BulkItemProcessor>.Instance);
            var claimed = (await _bulkRepository.GetItemAsync(request.Id, item.Id))!;
            var outcome = await processor.ProcessAsync(claimed, request, CancellationToken.None);
            Context.ChangeTracker.Clear();
            return (outcome, (await _bulkRepository.GetItemAsync(request.Id, item.Id))!);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CurrentUser(string userId) : ICurrentUserService
    {
        public string? UserId => userId;
        public string? Username => userId;
        public string? Role => "Recruiter";
        public string? Department => null;
        public bool IsAdmin => false;
        public Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<CurrentAuthorizationState?>(null);
    }

    private sealed class Source(byte[] content) : ISharePointSource
    {
        public Task<SourceItemMetadata?> GetItemMetadataAsync(
            string driveId,
            string itemId,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceItemMetadata?>(new("etag", content.LongLength));

        public Task<byte[]> DownloadAsync(
            string driveId,
            string itemId,
            long maxBytes,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(content);

        public bool CanConvertToPdf(string extension) => false;

        public Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InlineContentStore : IBulkContentStore
    {
        public Task<BulkScoringItemContent> StageAsync(
            string requestId,
            string itemId,
            BulkContentKind kind,
            string fileName,
            string mimeType,
            byte[] bytes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BulkScoringItemContent
            {
                ItemId = itemId,
                Kind = kind,
                ContentBase64 = Convert.ToBase64String(bytes),
                SizeBytes = bytes.LongLength,
                Sha256 = ContentFingerprint.Compute(bytes),
            });

        public Task<byte[]> ReadAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default) =>
            Task.FromResult(Convert.FromBase64String(content.ContentBase64!));
        public Task DeleteAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByRequestAsync(string requestId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoRasterizer : IDocumentRasterizer
    {
        public Task<RasterizationResult> RasterizeAsync(
            byte[] source,
            string mimeType,
            RasterizationOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fingerprint tests must not rasterize.");
    }
}
