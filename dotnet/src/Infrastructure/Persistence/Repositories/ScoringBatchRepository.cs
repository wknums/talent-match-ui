using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class ScoringBatchRepository : IScoringBatchRepository
{
    private readonly AppDbContext _db;
    public ScoringBatchRepository(AppDbContext db) => _db = db;

    /// <summary>
    /// Schema-qualified table name for raw SQL. SQL Server uses [talentmatch].
    /// SQLite has no schemas, so the bare table name is used.
    /// </summary>
    private string Batches => _db.Database.IsSqlServer() ? "[talentmatch].[ScoringBatches]" : "ScoringBatches";
    private string Progress => _db.Database.IsSqlServer() ? "[talentmatch].[ScoringJobProgress]" : "ScoringJobProgress";

    public async Task<ScoringBatch> CreateAsync(ScoringBatch batch, CancellationToken ct = default)
    {
        batch.CreatedAt = DateTime.UtcNow;
        batch.UpdatedAt = batch.CreatedAt;
        if (batch.NextPollAt == default) batch.NextPollAt = batch.CreatedAt;
        _db.ScoringBatches.Add(batch);
        await _db.SaveChangesAsync(ct);
        return batch;
    }

    public Task<ScoringBatch?> GetByIdAsync(string batchId, CancellationToken ct = default)
        => _db.ScoringBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct);

    public async Task<IReadOnlyList<ScoringBatch>> ListByJobAsync(string jobId, CancellationToken ct = default)
        => await _db.ScoringBatches.AsNoTracking()
            .Where(b => b.JobId == jobId)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ScoringBatch>> FindDueCandidatesAsync(int limit, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _db.ScoringBatches.AsNoTracking()
            .Where(b => (b.Status == "pending" || b.Status == "submitted" || b.CancelRequested)
                        && b.NextPollAt <= now
                        && (b.LeaseOwner == null || b.LeasedUntil == null || b.LeasedUntil < now))
            .OrderBy(b => b.NextPollAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<bool> AcquireLeaseAsync(string batchId, string owner, int leaseSeconds, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var until = now.AddSeconds(leaseSeconds);
        // Cross-provider conditional update — pure parameter comparisons, no date functions.
        var rows = await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET LeaseOwner = {{0}}, LeasedUntil = {{1}}, UpdatedAt = {{2}}
              WHERE BatchId = {{3}}
                AND (LeaseOwner IS NULL OR LeasedUntil IS NULL OR LeasedUntil < {{4}})",
            owner, until, now, batchId, now);
        return rows > 0;
    }

    public async Task ReleaseLeaseAsync(string batchId, CancellationToken ct = default)
    {
        await _db.Database.ExecuteSqlRawAsync(
            $"UPDATE {Batches} SET LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = {{0}} WHERE BatchId = {{1}}",
            DateTime.UtcNow, batchId);
    }

    public async Task MarkSubmittedAsync(string batchId, string submissionId, string? pollUrl, DateTime nextPollAt, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET Status = 'submitted', SubmissionId = {{0}}, PollUrl = {{1}},
                  SubmittedAt = {{2}}, NextPollAt = {{3}}, LastError = NULL,
                  LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = {{4}}
              WHERE BatchId = {{5}}",
            submissionId, (object?)pollUrl ?? DBNull.Value, now, nextPollAt, now, batchId);
    }

    public async Task SetNextPollAtAsync(string batchId, DateTime nextPollAt, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET NextPollAt = {{0}}, LastPolledAt = {{1}},
                  LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = {{2}}
              WHERE BatchId = {{3}}",
            nextPollAt, now, now, batchId);
    }

    public async Task MarkCompletedAsync(string batchId, string resultJson, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET Status = 'completed', ResultJson = {{0}}, LastPolledAt = {{1}},
                  LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = {{2}}
              WHERE BatchId = {{3}}",
            resultJson, now, now, batchId);
    }

    public async Task MarkFailedAsync(string batchId, string error, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET Status = 'failed', LastError = {{0}}, LastPolledAt = {{1}},
                  LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = {{2}}
              WHERE BatchId = {{3}}",
            error, now, now, batchId);
    }

    public async Task MarkCancelledAsync(string batchId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET Status = 'cancelled', LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = {{0}}
              WHERE BatchId = {{1}}",
            now, batchId);
    }

    public async Task IncrementAttemptAsync(string batchId, string error, DateTime nextPollAt, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET Attempt = Attempt + 1, LastError = {{0}}, NextPollAt = {{1}},
                  LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = {{2}}
              WHERE BatchId = {{3}}",
            error, nextPollAt, now, batchId);
    }

    public async Task<int> RequestCancelByJobAsync(string jobId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _db.Database.ExecuteSqlRawAsync(
            $@"UPDATE {Batches}
              SET CancelRequested = 1, UpdatedAt = {{0}}
              WHERE JobId = {{1}} AND Status IN ('pending','submitting','submitted')",
            now, jobId);
    }

    public async Task InitProgressAsync(string jobId, int totalApps, int batchesPending, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var existing = await _db.ScoringJobProgress.FirstOrDefaultAsync(p => p.JobId == jobId, ct);
        if (existing == null)
        {
            _db.ScoringJobProgress.Add(new ScoringJobProgress
            {
                JobId = jobId,
                TotalApps = totalApps,
                BatchesPending = batchesPending,
                StartedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.TotalApps = totalApps;
            existing.BatchesPending = batchesPending;
            existing.BatchesSubmitted = 0;
            existing.BatchesCompleted = 0;
            existing.BatchesFailed = 0;
            existing.AppsCompleted = 0;
            existing.AppsFailed = 0;
            existing.CancelRequested = false;
            existing.StartedAt = now;
            existing.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    public Task<ScoringJobProgress?> GetProgressAsync(string jobId, CancellationToken ct = default)
        => _db.ScoringJobProgress.AsNoTracking().FirstOrDefaultAsync(p => p.JobId == jobId, ct);

    public async Task RequestCancelProgressAsync(string jobId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlRawAsync(
            $"UPDATE {Progress} SET CancelRequested = 1, UpdatedAt = {{0}} WHERE JobId = {{1}}",
            now, jobId);
    }

    public async Task ApplyTransitionAsync(
        string jobId,
        string? fromStatus,
        string? toStatus,
        int appsCompletedDelta,
        int appsFailedDelta,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // Decrement the "from" counter (if any) and increment the "to" counter (if any).
        // Done in a single UPDATE — counters are int, no race except eventual consistency
        // which the reconciler tolerates.
        var sets = new List<string>();
        var args = new List<object>();
        int idx = 0;

        void AddSet(string col, int delta)
        {
            if (delta == 0) return;
            sets.Add($"{col} = {col} + {{{idx}}}");
            args.Add(delta);
            idx++;
        }

        if (fromStatus == "pending") AddSet("BatchesPending", -1);
        else if (fromStatus == "submitted") AddSet("BatchesSubmitted", -1);

        if (toStatus == "pending") AddSet("BatchesPending", 1);
        else if (toStatus == "submitted") AddSet("BatchesSubmitted", 1);
        else if (toStatus == "completed") AddSet("BatchesCompleted", 1);
        else if (toStatus == "failed") AddSet("BatchesFailed", 1);

        AddSet("AppsCompleted", appsCompletedDelta);
        AddSet("AppsFailed", appsFailedDelta);

        if (sets.Count == 0) return;

        sets.Add($"UpdatedAt = {{{idx}}}");
        args.Add(now);
        idx++;
        args.Add(jobId);

        var sql = $"UPDATE {Progress} SET {string.Join(", ", sets)} WHERE JobId = {{{idx}}}";
        await _db.Database.ExecuteSqlRawAsync(sql, args.ToArray());
    }
}
