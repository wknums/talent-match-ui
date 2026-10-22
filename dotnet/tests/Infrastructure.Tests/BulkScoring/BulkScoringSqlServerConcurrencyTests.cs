using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Services;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringPersistenceRetryTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkScoringSqlServerConcurrencyTests
{
    private const string ConnectionVariable = "BULK_SCORING_TEST_SQLSERVER";

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Parallel_staging_and_status_writes_use_request_first_locks_without_deadlocks(int requests)
    {
        await using var f = await SqlFixture.CreateAsync();
        var batches = new List<BulkScoringRequest>();
        for (var n = 0; n < requests; n++)
            batches.Add(await f.CreateRequestAsync(8 / requests));
        f.Commands.Enabled = true;
        IReadOnlyList<BulkScoringItem> claimed;
        await using (var db = f.Context())
            claimed = await f.Repository(db).ClaimDueItemsAsync("worker", 8, f.Now, TimeSpan.FromMinutes(5));
        claimed.Should().HaveCount(8);

        await Task.WhenAll(claimed.Select(async item =>
        {
            await using var db = f.Context();
            var repo = f.Repository(db);
            var request = batches.Single(r => r.Id == item.RequestId);
            item.TransitionTo(BulkItemStatus.Converting, f.Now);
            await repo.SaveItemWithAuditAsync(item, Audit(request, item, BulkAuditEventTypes.BulkItemConversionStarted));
            for (var attempt = 0; attempt < 4; attempt++)
                await repo.SaveItemAndContentsWithAuditsAsync(item,
                    [new BulkScoringItemContent
                    {
                        ItemId = item.Id, Kind = BulkContentKind.Original,
                        ContentBase64 = "AQ==", SizeBytes = 1, Sha256 = "test"
                    }],
                    [Audit(request, item, BulkAuditEventTypes.BulkOriginalContentStaged)]);
        })).WaitAsync(TimeSpan.FromSeconds(60));

        f.Commands.Enabled = false;
        f.Commands.Deadlocks.Should().Be(0);
        f.Commands.FirstCommands.Should().NotBeEmpty().And.OnlyContain(sql => sql.Contains("WITH (UPDLOCK, ROWLOCK)"));
        await using var verification = f.Context();
        (await verification.BulkScoringItemContents.CountAsync()).Should().Be(8);
        (await verification.BulkAuditEvents.CountAsync(e => e.EventType == BulkAuditEventTypes.BulkOriginalContentStaged))
            .Should().Be(8);
        foreach (var request in batches)
        {
            var counts = await f.Repository(verification).GetAsync(request.Id);
            counts!.ConvertingCount.Should().Be(8 / requests);
            counts.ImportingCount.Should().Be(0);
            (await f.Repository(verification).TryFinalizeAsync(request.Id, f.Now)).Should().BeNull();
        }
    }

    [SqlServerFact]
    public async Task Competing_fingerprint_claims_have_one_winner_without_deadlocks()
    {
        await using var f = await SqlFixture.CreateAsync();
        var request = await f.CreateRequestAsync(2);
        await using var read = f.Context();
        var items = await read.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == request.Id).ToListAsync();
        var claims = await Task.WhenAll(items.Select(async item =>
        {
            await using var db = f.Context();
            return await f.Repository(db).TryClaimFingerprintWithAuditAsync(
                item.Id, "same-fingerprint", Audit(request, item, BulkAuditEventTypes.BulkItemDuplicateClaimed));
        }));
        claims.Count(won => won).Should().Be(1);
        f.Commands.Deadlocks.Should().Be(0);
    }

    [SqlServerFact]
    public async Task Audit_index_script_and_migration_accept_an_already_deployed_index()
    {
        await using var f = await SqlFixture.CreateAsync();
        await using var db = f.Context();
        var migration = db.GetService<IMigrator>().GenerateScript(
            "20261019173434_AddUiPollingIntervals", "20261020150000_AddBulkAuditLookupIndex");
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "infra", "scripts", "sql", "add-bulk-audit-lookup-index.sql"));
        await db.Database.OpenConnectionAsync();
        foreach (var sql in new[]
                 {
                     db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(),
                     "DROP INDEX [IX_BulkAuditEvents_SubjectId_EventType] ON [talentmatch].[BulkAuditEvents];",
                     script, script, migration
                 })
            foreach (var batch in Regex.Split(sql, @"\r?\n\s*GO\s*(?:\r?\n|$)", RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = batch;
                    await command.ExecuteNonQueryAsync();
                }

        await using var columns = db.Database.GetDbConnection().CreateCommand();
        columns.CommandText = """
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'talentmatch.BulkAuditEvents')
              AND i.name = N'IX_BulkAuditEvents_SubjectId_EventType'
            ORDER BY ic.key_ordinal;
            """;
        var names = new List<string>();
        await using var reader = await columns.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        names.Should().Equal("SubjectId", "EventType");
    }

    [SqlServerFact]
    public async Task Repeated_startup_upgrade_preserves_current_data_without_bulk_writes_or_constraint_rebuilds()
    {
        await using var f = await SqlFixture.CreateAsync();
        var request = await f.CreateRequestAsync(2);
        await using var db = f.Context();
        await db.BulkScoringRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.IncludedItemCount, 1)
            .SetProperty(r => r.IncludedBytes, 1));
        await db.BulkScoringItems.Where(i => i.RequestId == request.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(i => i.ScoredVersion, BulkScoredVersion.Converted)
            .SetProperty(i => i.OriginalPageCount, 3)
            .SetProperty(i => i.ConvertedPageCount, 2));
        await db.Database.OpenConnectionAsync();
        foreach (var sql in new[]
                 {
                     """
                     CREATE TRIGGER [talentmatch].[RejectRepeatedRequestUpdates]
                     ON [talentmatch].[BulkScoringRequests] AFTER UPDATE
                     AS THROW 51042, 'Startup must not update current bulk requests.', 1;
                     """,
                     """
                     CREATE TRIGGER [talentmatch].[RejectRepeatedItemUpdates]
                     ON [talentmatch].[BulkScoringItems] AFTER UPDATE
                     AS THROW 51042, 'Startup must not update current bulk items.', 1;
                     """,
                     """
                     CREATE TRIGGER [RejectRepeatedBulkAlter] ON DATABASE FOR ALTER_TABLE
                     AS THROW 51042, 'Startup must not rebuild current bulk constraints.', 1;
                     """,
                 })
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        var upgrade = File.ReadAllText(Path.Combine(RepositoryRoot(), "server", "storage", "schema-bulk-v3-upgrades.sql"));
        for (var repeat = 0; repeat < 2; repeat++)
            foreach (var batch in Regex.Split(upgrade, @"\r?\n\s*GO\s*(?:\r?\n|$)", RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = batch;
                    await command.ExecuteNonQueryAsync();
                }

        var retained = await db.BulkScoringRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
        retained.IncludedItemCount.Should().Be(1);
        retained.IncludedBytes.Should().Be(1);
        (await db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == request.Id).ToListAsync())
            .Should().OnlyContain(i => i.OriginalPageCount == 3 && i.ConvertedPageCount == 2);
    }

    [SqlServerFact]
    public async Task Pending_scoring_API_holds_no_transaction_or_application_row_lock()
    {
        await using var f = await SqlFixture.CreateAsync();
        await using var db = f.Context();
        var owner = "scoring-worker";
        db.Applications.Add(new TalentMatch.Domain.Entities.Application
        {
            Id = "pending-api", JobId = "job", CandidateRef = "candidate", Status = "Scoring",
            ScoringOwner = owner, ScoringLeaseUntil = DateTime.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        var sender = new PendingScoringResponse();
        var queue = new SequentialScoringQueueRepository(db);
        var scorer = new SequentialApplicationScorer(
            sender, queue, new ApplicationScoringFinalizer(new ApplicationRepository(db)));
        var work = new SequentialScoringWork("pending-api", "job", owner, "prompt", 1, "Job", "[]", 15, 70);
        var scoring = scorer.ScoreAsync(work, CancellationToken.None);
        try
        {
            scoring.IsCompleted.Should().BeFalse();
            db.Database.CurrentTransaction.Should().BeNull();
            await using var concurrent = f.Context();
            concurrent.Database.SetCommandTimeout(3);
            (await new SequentialScoringQueueRepository(concurrent).RenewLeaseAsync(
                work.ApplicationId, owner, DateTime.UtcNow, TimeSpan.FromMinutes(5))
                .WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await concurrent.Applications.Where(a => a.Id == work.ApplicationId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CandidateName, "updated while API pending"))
                .WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
            (await concurrent.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Scoring");
        }
        finally
        {
            sender.Response.TrySetResult(new ScoreApplicationResult([], null));
            await scoring;
        }
    }

    private static string RepositoryRoot()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, "server", "storage", "schema.sql"))
                && File.Exists(Path.Combine(root.FullName, "infra", "scripts", "sql", "add-bulk-audit-lookup-index.sql")))
                return root.FullName;
        throw new DirectoryNotFoundException("Shared SQL schema was not found.");
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to enable isolated SQL Server tests.";
        }
    }

    private sealed class SqlServerTheoryAttribute : TheoryAttribute
    {
        public SqlServerTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to enable isolated SQL Server tests.";
        }
    }

    private sealed class PendingScoringResponse : ISender
    {
        public TaskCompletionSource<ScoreApplicationResult> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ScoreApplicationCommand { PersistResults: false })
                throw new InvalidOperationException("Only non-persisting scoring is expected.");
            return (TResponse)(object)await Response.Task.WaitAsync(cancellationToken);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SqlFixture(string connectionString) : IAsyncDisposable
    {
        public DateTime Now { get; } = DateTime.UtcNow;
        public Commands Commands { get; } = new();

        public AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), [1205])
                .CommandTimeout(10))
            .AddInterceptors(Commands).Options);

        public BulkScoringRepository Repository(AppDbContext db) =>
            new(db, new BulkScoringRepositoryTests.FixedClock(Now));

        public static async Task<SqlFixture> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable))
            {
                InitialCatalog = $"TalentMatchDeadlock_{Guid.NewGuid():N}"
            };
            var fixture = new SqlFixture(connection.ConnectionString);
            try
            {
                await using var db = fixture.Context();
                db.Database.SetCommandTimeout(120);
                await db.GetService<IRelationalDatabaseCreator>().CreateAsync();
                await db.Database.OpenConnectionAsync();
                async Task ExecuteBatchAsync(string batch)
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = batch;
                    command.CommandTimeout = 120;
                    await command.ExecuteNonQueryAsync();
                }
                var storage = Path.Combine(RepositoryRoot(), "server", "storage");
                // Exercise the deployed shared schema, including its delegated bulk upgrade.
                foreach (var batch in Regex.Split(File.ReadAllText(Path.Combine(storage, "schema.sql")),
                             @"\r?\n(?=IF NOT EXISTS|CREATE (?:UNIQUE )?INDEX)"))
                    if (!string.IsNullOrWhiteSpace(batch))
                        await ExecuteBatchAsync(batch);
                foreach (var file in new[] { "schema-bulk-v3-upgrades.sql", "schema-pre-batch-upgrades.sql" })
                    foreach (var batch in Regex.Split(File.ReadAllText(Path.Combine(storage, file)),
                                 @"\r?\n\s*GO\s*(?:\r?\n|$)", RegexOptions.IgnoreCase))
                        if (!string.IsNullOrWhiteSpace(batch))
                            await ExecuteBatchAsync(batch);
                db.Jobs.Add(new Job { Id = "job", Title = "Job" });
                await db.SaveChangesAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<BulkScoringRequest> CreateRequestAsync(int count)
        {
            await using var db = Context();
            var repo = Repository(db);
            var request = new BulkScoringRequest
            {
                JobId = "job", OwnerActorId = "actor", SourceKind = BulkSourceKind.SharePoint,
                SourceTenantId = "tenant", SourceAccountBindingHash = "account",
                SourceDriveId = "drive", SourceFolderItemId = "folder", SourceFolderName = "Candidates",
                SourceFolderPath = "/Candidates", SourceWebUrl = "https://example.test",
                RasterDpi = 150, MaxFilesPerRequest = 64, MaxBytesPerFile = 1024, MaxTotalBytes = 1_048_576,
                DiscoveryStepMaxFiles = 64, DiscoveryStepMaxEntries = 100, DiscoveryStepMaxSeconds = 45
            };
            var items = Enumerable.Range(0, count).Select(n => new BulkScoringItem
            {
                Ordinal = n, SourceDriveId = "drive", SourceDriveItemId = $"source-{n}",
                FileName = $"file-{n}.pdf", MimeType = "application/pdf", SizeBytes = 1
            }).ToList();
            await repo.CreateDraftAsync(request,
                new(items, new Dictionary<BulkReasonCode, int>(), 0, count,
                    BulkDiscoveryStatus.Complete, null, Now.AddMinutes(30)));
            return await repo.ConfirmAsync(request.Id, request.ConcurrencyVersion, false, false, "config", null, Now);
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = Context();
            db.Database.SetCommandTimeout(120);
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class Commands : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<Guid, string> _firstCommands = new();
        private int _deadlocks;
        public bool Enabled { get; set; }
        public int Deadlocks => _deadlocks;
        public IEnumerable<string> FirstCommands => _firstCommands.Values;

        private void Record(DbCommand command, CommandEventData data)
        {
            if (Enabled && data.Context?.Database.CurrentTransaction is { } tx)
                _firstCommands.TryAdd(tx.TransactionId, command.CommandText);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number == 1205))
                Interlocked.Increment(ref _deadlocks);
            return Task.CompletedTask;
        }
    }
}
