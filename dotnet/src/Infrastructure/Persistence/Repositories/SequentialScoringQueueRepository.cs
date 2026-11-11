using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TalentMatch.Application.Rubrics.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using ApplicationEntity = TalentMatch.Domain.Entities.Application;
using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class SequentialScoringQueueRepository(
    AppDbContext db) : ISequentialScoringQueueRepository
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
                let scoringConfigVersionId = db.BulkScoringItems
                    .Where(item => item.ApplicationId == application.Id
                        && item.ScoringConfigVersionId != null)
                    .Select(item => item.ScoringConfigVersionId)
                    .FirstOrDefault() ?? application.Job!.CurrentConfigVersionId
                join config in db.JobConfigVersions
                    on scoringConfigVersionId equals config.Id
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
                            && ((prompt.ModelId == ""
                                    || prompt.ModelId.ToLower() == "passthrough-llm")
                                || (prompt.ReasoningLevel != ""
                                    && prompt.ApprovedModelId == prompt.ModelId
                                    && prompt.ApprovedReasoningLevel == prompt.ReasoningLevel
                                    && db.PromptTestRuns.Any(run =>
                                        run.Id == prompt.ApprovedTestRunId
                                        && run.PromptId == prompt.Id
                                        && run.Status == "approved"
                                        && run.ModelId == prompt.ModelId
                                        && run.ReasoningLevel == prompt.ReasoningLevel
                                        && run.ApprovedModelId == prompt.ModelId
                                        && run.ApprovedReasoningLevel == prompt.ReasoningLevel)))
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
                        && (db.BulkScoringItems.Any(item =>
                                item.ApplicationId == application.Id
                                && item.ScoringConfigVersionId == candidate.ConfigId)
                            || (!db.BulkScoringItems.Any(item =>
                                    item.ApplicationId == application.Id
                                    && item.ScoringConfigVersionId != null)
                                && application.Job.CurrentConfigVersionId == candidate.ConfigId))
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
                            && ((prompt.ModelId == ""
                                    || prompt.ModelId.ToLower() == "passthrough-llm")
                                || (prompt.ReasoningLevel != ""
                                    && prompt.ApprovedModelId == prompt.ModelId
                                    && prompt.ApprovedReasoningLevel == prompt.ReasoningLevel
                                    && db.PromptTestRuns.Any(run =>
                                        run.Id == prompt.ApprovedTestRunId
                                        && run.PromptId == prompt.Id
                                        && run.Status == "approved"
                                        && run.ModelId == prompt.ModelId
                                        && run.ReasoningLevel == prompt.ReasoningLevel
                                        && run.ApprovedModelId == prompt.ModelId
                                        && run.ApprovedReasoningLevel == prompt.ReasoningLevel)))
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
                        candidate.VarianceThreshold, candidate.LonglistThreshold,
                        candidate.ConfigId, until);
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
        string applicationId, string owner, ApplicationScoringFinalization result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Aggregate.ApplicationId != applicationId
            || result.Runs.Any(run => run.ApplicationId != applicationId))
            throw new ArgumentException("Scoring results must belong to the finalized application.", nameof(result));
        if (result.Status is not ("Completed" or "NeedsManualReview"))
            throw new ArgumentException("Scoring finalization requires a terminal scoring status.", nameof(result));

        return InTransactionAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var hasCandidateName = !string.IsNullOrWhiteSpace(result.CandidateName);
            // The conditional terminal write is also the ownership fence and lease release.
            // Dependent results commit with it, but preparation and reads hold no write locks.
            var fenced = await Owned(applicationId, owner)
                .Where(application => application.ScoringLeaseUntil > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(application => application.Status, result.Status)
                    .SetProperty(application => application.FinalScore, result.Aggregate.FinalScore)
                    .SetProperty(application => application.Variance, result.Aggregate.Variance)
                    .SetProperty(application => application.FinalDecision, result.Aggregate.Decision)
                    .SetProperty(application => application.CandidateName,
                        application => hasCandidateName ? result.CandidateName : application.CandidateName)
                    .SetProperty(application => application.ScoringOwner, (string?)null)
                    .SetProperty(application => application.ScoringLeaseUntil, (DateTime?)null)
                    .SetProperty(application => application.UpdatedAt, now), ct);
            if (fenced != 1)
            {
                db.ChangeTracker.Clear();
                return false;
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, ct, async () =>
        {
            db.ChangeTracker.Clear();
            var aggregate = result.Aggregate;
            var existing = await db.AggregatedResults.SingleOrDefaultAsync(row => row.ApplicationId == applicationId, ct);
            if (existing is null)
                db.AggregatedResults.Add(aggregate);
            else
            {
                existing.FinalScore = aggregate.FinalScore;
                existing.Variance = aggregate.Variance;
                existing.Decision = aggregate.Decision;
                existing.Confidence = aggregate.Confidence;
                existing.ConsolidatedRationale = aggregate.ConsolidatedRationale;
                existing.MergedImprovementTipsJson = aggregate.MergedImprovementTipsJson;
                existing.FinalSubScoresJson = aggregate.FinalSubScoresJson;
            }
            db.ScoringRuns.AddRange(result.Runs);
        });
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

    private Task<T> InTransactionAsync<T>(
        Func<Task<T>> action, CancellationToken ct, Func<Task>? prepare = null)
    {
        IExecutionStrategy strategy = db.Database.IsSqlServer()
            ? new SequentialScoringExecutionStrategy(db)
            : db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(action, async (operation, token) =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (prepare is not null)
                    await prepare();
                await using var transaction = await db.Database.BeginTransactionAsync(token);
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
    }

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
            // Approval validates rubric contents; admission recognizes the supported persisted shapes.
            return RubricJsonReader.HasScoringRubric(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
