using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.Rubrics.Models;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using ApplicationEntity = TalentMatch.Domain.Entities.Application;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class SequentialScoringQueueRepository(AppDbContext db) : ISequentialScoringQueueRepository
{
    private const string ExpiredLeaseError = "Sequential scoring lease expired; scoring outcome is unknown. Manual retry is required.";

    private IQueryable<ApplicationEntity> EligibleApplications =>
        db.Applications.Where(application =>
            application.Status == "Queued"
            && application.TestRunId == null
            && application.ScoringOwner == null
            && application.Job != null
            && application.Job.Status.ToLower() == "active"
            && application.Documents.Any(document =>
                (document.BlobUri != null && document.BlobUri.Replace("\r", "").Replace("\n", "").Replace("\t", "").Trim() != "")
                || db.DocumentBlobs.Any(blob => blob.DocumentId == document.Id
                    && blob.Content.Replace("\r", "").Replace("\n", "").Replace("\t", "").Trim() != "")));

    public async Task<SequentialScoringWork?> TryClaimAsync(
        string owner, DateTime now, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        ValidateLease(owner, leaseDuration);
        var until = now.Add(leaseDuration);
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = await (
                from application in EligibleApplications
                join config in db.JobConfigVersions
                    on application.Job!.CurrentConfigVersionId equals config.Id
                where config.JobId == application.JobId
                    && config.RubricApprovalStatus.ToLower() == "approved"
                    && config.ScoringRunCount > 0
                    && config.RubricJson.Trim() != ""
                orderby application.CreatedAt, application.Id
                select new
                {
                    ApplicationId = application.Id,
                    application.JobId,
                    ConfigId = config.Id,
                    JobDescription = application.Job!.JobDescription ?? "",
                    config.RubricJson,
                    config.ScoringRunCount,
                    config.VarianceThreshold,
                    config.LonglistThreshold,
                    PromptId = db.ScoringPrompts
                        .Where(prompt => prompt.JobId == application.JobId
                            && prompt.Status.ToLower() == "production-approved"
                            && prompt.PromptText.Replace("\r", "").Replace("\n", "").Replace("\t", "").Trim() != "")
                        .OrderByDescending(prompt => prompt.VersionNumber)
                        .ThenBy(prompt => prompt.Id)
                        .Select(prompt => prompt.Id).FirstOrDefault()
                }).AsNoTracking().Skip(offset).Take(32).ToListAsync(ct);

            foreach (var candidate in candidates)
            {
                if (candidate.PromptId is null || !HasRubric(candidate.RubricJson))
                    continue;

                // The snapshot and eligibility are rechecked in the conditional write, not just
                // the preceding read. Competing request/provider scopes cannot both claim a row.
                var updated = await EligibleApplications
                    .Where(application => application.Id == candidate.ApplicationId
                        && (application.Job!.JobDescription ?? "") == candidate.JobDescription
                        && application.Job.CurrentConfigVersionId == candidate.ConfigId
                        && db.JobConfigVersions.Any(config =>
                            config.Id == candidate.ConfigId
                            && config.JobId == application.JobId
                            && config.RubricApprovalStatus.ToLower() == "approved"
                            && config.RubricJson == candidate.RubricJson
                            && config.ScoringRunCount == candidate.ScoringRunCount
                            && config.VarianceThreshold == candidate.VarianceThreshold
                            && config.LonglistThreshold == candidate.LonglistThreshold)
                        && db.ScoringPrompts.Any(prompt =>
                            prompt.Id == candidate.PromptId
                            && prompt.JobId == application.JobId
                            && prompt.Status.ToLower() == "production-approved"
                            && prompt.PromptText.Replace("\r", "").Replace("\n", "").Replace("\t", "").Trim() != ""))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(application => application.Status, "Scoring")
                        .SetProperty(application => application.ScoringOwner, owner)
                        .SetProperty(application => application.ScoringLeaseUntil, until)
                        .SetProperty(application => application.LastError, (string?)null)
                        .SetProperty(application => application.UpdatedAt, now), ct);

                if (updated == 1)
                {
                    DetachApplication(candidate.ApplicationId);
                    return new SequentialScoringWork(
                        candidate.ApplicationId, candidate.JobId, owner, candidate.PromptId,
                        candidate.ScoringRunCount, candidate.JobDescription, candidate.RubricJson,
                        candidate.VarianceThreshold, candidate.LonglistThreshold);
                }
            }

            if (candidates.Count < 32)
                return null;
            offset += candidates.Count;
        }
    }

    public async Task<bool> RenewLeaseAsync(
        string applicationId, string owner, DateTime now, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        ValidateLease(owner, leaseDuration);
        var until = now.Add(leaseDuration);
        return await Owned(applicationId, owner)
            .Where(application => application.ScoringLeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(application => application.ScoringLeaseUntil,
                    application => application.ScoringLeaseUntil > until ? application.ScoringLeaseUntil : until), ct) == 1;
    }

    public Task<bool> FinalizeAsync(
        string applicationId, string owner, Func<CancellationToken, Task> persistResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persistResult);
        return InTransactionAsync(async () =>
        {
            var now = DateTime.UtcNow;
            // Even a no-op UPDATE obtains a write lock until commit. Recovery/failure cannot
            // revoke ownership between this fence and the callback's writes on this DbContext.
            var fenced = await Owned(applicationId, owner)
                .Where(application => application.ScoringLeaseUntil > now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(application => application.ScoringOwner, owner), ct);
            if (fenced != 1)
                return false;

            DetachApplication(applicationId);
            await persistResult(ct);
            await db.SaveChangesAsync(ct);
            var released = await db.Applications
                .Where(application => application.Id == applicationId && application.ScoringOwner == owner
                    && application.Status != "Scoring" && application.Status != "Queued" && application.Status != "Uploading")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(application => application.ScoringOwner, (string?)null)
                    .SetProperty(application => application.ScoringLeaseUntil, (DateTime?)null), ct);
            if (released != 1)
                throw new DbUpdateConcurrencyException("Sequential scoring ownership was lost or the callback did not finalize the application.");
            DetachApplication(applicationId);
            return true;
        }, ct);
    }

    public async Task FailAsync(
        string applicationId, string owner, string error, int failureCount, CancellationToken ct = default)
    {
        await InTransactionAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var failed = await MarkFailedAsync(Owned(applicationId, owner), error, now, ct);
            if (failed == 1)
                await AddFailureAsync(applicationId, error, failureCount, now, ct);
            return failed;
        }, ct);
    }

    public async Task RecoverExpiredAsync(DateTime now, CancellationToken ct = default)
    {
        // Read outside a transaction; each conditional transition + DLQ insert is a short unit.
        // A heartbeat after this read makes the update ineligible, preserving live work.
        var expired = await db.Applications.AsNoTracking()
            .Where(application => application.Status == "Scoring"
                && application.ScoringOwner != null && application.ScoringLeaseUntil <= now)
            .Select(application => new { application.Id, application.ScoringOwner })
            .ToListAsync(ct);

        foreach (var application in expired)
        {
            ct.ThrowIfCancellationRequested();
            await InTransactionAsync(async () =>
            {
                var failed = await MarkFailedAsync(
                    Owned(application.Id, application.ScoringOwner!)
                        .Where(row => row.ScoringLeaseUntil <= now), ExpiredLeaseError, now, ct);
                if (failed == 1)
                    await AddFailureAsync(application.Id, ExpiredLeaseError, 0, now, ct);
                return failed;
            }, ct);
        }
    }

    private IQueryable<ApplicationEntity> Owned(string applicationId, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return db.Applications.Where(application => application.Id == applicationId
            && application.Status == "Scoring" && application.ScoringOwner == owner);
    }

    public Task<bool> RetryAsync(string itemId, CancellationToken ct = default) =>
        InTransactionAsync(async () =>
        {
            var item = await db.FailureQueueItems.AsNoTracking()
                .SingleOrDefaultAsync(failure => failure.Id == itemId && failure.EntityType == "Application", ct);
            if (item is null)
                return false;
            var now = DateTime.UtcNow;
            var retried = await db.Applications
                .Where(application => application.Id == item.EntityId
                    && application.TestRunId == null
                    && (application.Status == "ScoringFailed" || application.Status == "ExtractionFailed")
                    && application.ScoringOwner == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(application => application.Status, "Queued")
                    .SetProperty(application => application.LastError, (string?)null)
                    .SetProperty(application => application.ScoringLeaseUntil, (DateTime?)null)
                    .SetProperty(application => application.UpdatedAt, now), ct);
            if (retried != 1)
                return false;
            var removed = await db.FailureQueueItems.Where(failure => failure.Id == itemId).ExecuteDeleteAsync(ct);
            if (removed != 1)
                throw new DbUpdateConcurrencyException("The scoring failure was already retried.");
            DetachApplication(item.EntityId);
            return true;
        }, ct);

    private Task<int> MarkFailedAsync(
        IQueryable<ApplicationEntity> applications, string error, DateTime now, CancellationToken ct) =>
        applications.ExecuteUpdateAsync(setters => setters
            .SetProperty(application => application.Status, "ScoringFailed")
            .SetProperty(application => application.LastError, error)
            .SetProperty(application => application.ScoringOwner, (string?)null)
            .SetProperty(application => application.ScoringLeaseUntil, (DateTime?)null)
            .SetProperty(application => application.UpdatedAt, now), ct);

    private async Task AddFailureAsync(
        string applicationId, string error, int failureCount, DateTime now, CancellationToken ct)
    {
        DetachApplication(applicationId);
        db.FailureQueueItems.Add(new FailureQueueItem
        {
            EntityType = "Application",
            EntityId = applicationId,
            FailureReason = error,
            RetryCount = failureCount,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    private Task<T> InTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(action, async (operation, token) =>
        {
            token.ThrowIfCancellationRequested();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            try
            {
                var result = await operation();
                await transaction.CommitAsync(token);
                return result;
            }
            catch
            {
                // SaveChanges may have accepted tracked values before a later write failed.
                // They must not leak into a retry after the transaction is rolled back.
                db.ChangeTracker.Clear();
                throw;
            }
        }, ct);

    private void DetachApplication(string applicationId)
    {
        var tracked = db.Applications.Local.FirstOrDefault(application => application.Id == applicationId);
        if (tracked is not null)
            db.Entry(tracked).State = EntityState.Detached;
    }

    private static void ValidateLease(string owner, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(owner));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    private static bool HasRubric(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return root.GetArrayLength() > 0;

            // Approval validates rubric contents; admission recognizes the supported persisted shapes.
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                && version.ValueEquals(RubricSchemaVersions.RubricV2)
                && root.TryGetProperty("categories", out var categories)
                && categories.ValueKind == JsonValueKind.Array
                && categories.GetArrayLength() > 0
                && root.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
