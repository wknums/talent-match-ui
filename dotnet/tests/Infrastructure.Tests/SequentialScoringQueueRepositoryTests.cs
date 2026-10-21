using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using TalentMatch.Application.Common.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using ApplicationEntity = TalentMatch.Domain.Entities.Application;

namespace TalentMatch.Infrastructure.Tests;

public sealed class SequentialScoringQueueRepositoryTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const string RubricV2 = """
        {"schemaVersion":"rubric-v2","categories":[{"id":"experience","name":"Experience","weight":1,"order":0}],"items":[]}
        """;

    [Fact]
    public async Task Claim_AcceptsThePascalCaseRubricShapePersistedByBlazor()
    {
        const string rubric = """
            {"SchemaVersion":"rubric-v2","LegacySourceVersionId":null,
             "Categories":[{"Id":"experience","Name":"Experience","Weight":1,"Order":0}],
             "Items":[{"Id":"item-1","CategoryId":"experience","Text":"Relevant experience",
                       "RequirementType":"experience","Order":0,"ReviewStatus":"confirmed","CreatedFrom":"manual"}]}
            """;
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync(rubric: rubric);
        await using var context = database.Open();

        var work = await new SequentialScoringQueueRepository(context).TryClaimAsync("owner", DateTime.UtcNow, Lease);

        work.Should().NotBeNull();
        work!.ApplicationId.Should().Be(id);
        work.RubricJson.Should().Be(rubric);
        (await context.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Scoring");
    }

    [Theory]
    [InlineData("expected-valid-rubric-v2.json")]
    [InlineData("expected-legacy-conversion.json")]
    public async Task Claim_AcceptsApprovedRubricV2FixturesAndPreservesTheirSnapshot(string fixture)
    {
        var rubric = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(), "specs", "001-dynamic-rubric-editor", "contracts", "fixtures", fixture));
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync(rubric: rubric);
        await using var context = database.Open();

        var work = await new SequentialScoringQueueRepository(context).TryClaimAsync("owner", DateTime.UtcNow, Lease);

        work.Should().NotBeNull();
        work!.ApplicationId.Should().Be(id);
        work.RubricJson.Should().Be(rubric);
        var application = await context.Applications.AsNoTracking().SingleAsync(row => row.Id == id);
        application.Status.Should().Be("Scoring");
        application.ScoringOwner.Should().Be("owner");
    }

    [Theory]
    [InlineData("""{"schemaVersion":"rubric-v3","categories":[{}],"items":[]}""")]
    [InlineData("""{"schemaVersion":2,"categories":[{}],"items":[]}""")]
    [InlineData("""{"schemaVersion":null,"categories":[{}],"items":[]}""")]
    [InlineData("""{"categories":[{}],"items":[]}""")]
    [InlineData("""{"schemaVersion":"rubric-v2","items":[]}""")]
    [InlineData("""{"schemaVersion":"rubric-v2","categories":[],"items":[]}""")]
    [InlineData("""{"schemaVersion":"rubric-v2","categories":null,"items":[]}""")]
    [InlineData("""{"schemaVersion":"rubric-v2","categories":{},"items":[]}""")]
    [InlineData("""{"schemaVersion":"rubric-v2","categories":[{}]}""")]
    [InlineData("""{"schemaVersion":"rubric-v2","categories":[{}],"items":null}""")]
    [InlineData("""{"schemaVersion":"rubric-v2","categories":[{}],"items":{}}""")]
    public async Task Claim_RejectsUnsupportedOrIncompleteRubricEnvelopesWithoutThrowing(string rubric)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync(rubric: rubric);
        await using var context = database.Open();

        var work = await new SequentialScoringQueueRepository(context).TryClaimAsync("owner", DateTime.UtcNow, Lease);

        work.Should().BeNull();
        var application = await context.Applications.AsNoTracking().SingleAsync(row => row.Id == id);
        application.Status.Should().Be("Queued");
        application.ScoringOwner.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Claim_RubricV2StillRequiresRubricAndPromptApproval(bool unapprovedRubric)
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync(rubric: RubricV2);
        await using var context = database.Open();
        if (unapprovedRubric)
            await context.JobConfigVersions.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RubricApprovalStatus, "draft"));
        else
            await context.ScoringPrompts.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "draft"));

        (await new SequentialScoringQueueRepository(context).TryClaimAsync("owner", DateTime.UtcNow, Lease))
            .Should().BeNull();
    }

    [Fact]
    public async Task Claim_IsUniqueAcrossIndependentScopesEvenWhenReadsRace()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        var barrier = new ClaimBarrier();
        await using var first = database.Open(barrier);
        await using var second = database.Open(barrier);
        var now = DateTime.UtcNow;

        var results = await Task.WhenAll(
            Task.Run(() => new SequentialScoringQueueRepository(first).TryClaimAsync("first", now, Lease)),
            Task.Run(() => new SequentialScoringQueueRepository(second).TryClaimAsync("second", now, Lease)));

        results.Count(result => result is not null).Should().Be(1);
        var winner = results.Single(result => result is not null)!;
        winner.ApplicationId.Should().Be(id);
        await using var verify = database.Open();
        var application = await verify.Applications.SingleAsync();
        application.Status.Should().Be("Scoring");
        application.ScoringOwner.Should().Be(winner.Owner);
        application.ScoringLeaseUntil.Should().Be(now + Lease);
    }

    [Fact]
    public async Task Claim_SelectsOldestAndCapturesApprovedConfiguration()
    {
        await using var database = await TestDatabase.CreateAsync();
        var newest = await database.SeedAsync(createdAt: DateTime.UtcNow);
        var oldest = await database.SeedAsync(createdAt: DateTime.UtcNow.AddHours(-1));
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);

        var work = await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);
        work!.ApplicationId.Should().Be(oldest);
        work.RunCount.Should().Be(3);
        work.JobDescription.Should().Be("Approved job description");
        work.RubricJson.Should().Be("""[{"name":"Experience","weight":100}]""");
        work.VarianceThreshold.Should().Be(12);
        work.LonglistThreshold.Should().Be(65);
        (await context.ScoringPrompts.SingleAsync(prompt => prompt.Id == work.PromptVersionId))
            .Status.Should().Be("production-approved");
        (await queue.TryClaimAsync("next", DateTime.UtcNow, Lease))!.ApplicationId.Should().Be(newest);
        (await queue.TryClaimAsync("last", DateTime.UtcNow, Lease)).Should().BeNull();
    }

    [Fact]
    public async Task Claim_UsesThePromptStoredProfileInsteadOfTheEnvironmentProfile()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();

        var work = await new SequentialScoringQueueRepository(context)
            .TryClaimAsync("owner", DateTime.UtcNow, Lease);

        work.Should().NotBeNull();
        work!.ApplicationId.Should().Be(id);
        var prompt = await context.ScoringPrompts.SingleAsync();
        prompt.ModelId.Should().Be("o3");
        prompt.ReasoningLevel.Should().Be("high");
    }

    [Fact]
    public async Task Claim_RejectsApprovalEvidenceForAnotherPromptProfile()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync();
        await using var context = database.Open();
        await context.PromptTestRuns.ExecuteUpdateAsync(setters => setters
            .SetProperty(run => run.ApprovedReasoningLevel, "low"));

        var work = await new SequentialScoringQueueRepository(context)
            .TryClaimAsync("owner", DateTime.UtcNow, Lease);

        work.Should().BeNull();
        (await context.Applications.SingleAsync()).Status.Should().Be("Queued");
    }

    [Fact]
    public async Task Claim_AcceptsLegacyPlaceholderProfileForRuntimeDefaultResolution()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        await context.ScoringPrompts.ExecuteUpdateAsync(setters => setters
            .SetProperty(prompt => prompt.ModelId, "passthrough-llm")
            .SetProperty(prompt => prompt.ReasoningLevel, "medium")
            .SetProperty(prompt => prompt.ApprovedModelId, (string?)null)
            .SetProperty(prompt => prompt.ApprovedReasoningLevel, (string?)null)
            .SetProperty(prompt => prompt.ApprovedTestRunId, (string?)null));

        var work = await new SequentialScoringQueueRepository(context)
            .TryClaimAsync("owner", DateTime.UtcNow, Lease);

        work.Should().NotBeNull();
        work!.ApplicationId.Should().Be(id);
    }

    [Theory]
    [InlineData("test-run")]
    [InlineData("uploading")]
    [InlineData("scored")]
    [InlineData("failed")]
    [InlineData("closed")]
    [InlineData("draft-job")]
    [InlineData("draft-rubric")]
    [InlineData("empty-rubric")]
    [InlineData("invalid-rubric")]
    [InlineData("object-rubric")]
    [InlineData("missing-config")]
    [InlineData("foreign-config")]
    [InlineData("no-runs")]
    [InlineData("no-prompt")]
    [InlineData("draft-prompt")]
    [InlineData("empty-prompt")]
    [InlineData("no-document")]
    [InlineData("missing-content")]
    [InlineData("empty-content")]
    [InlineData("owned")]
    public async Task Claim_ExcludesIneligibleApplications(string exclusion)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using (var setup = database.Open())
        {
            var application = await setup.Applications.SingleAsync();
            var job = await setup.Jobs.SingleAsync();
            var config = await setup.JobConfigVersions.SingleAsync();
            var prompt = await setup.ScoringPrompts.SingleAsync();
            switch (exclusion)
            {
                case "test-run": application.TestRunId = "test"; break;
                case "uploading": application.Status = "Uploading"; break;
                case "scored": application.Status = "Scored"; break;
                case "failed": application.Status = "ScoringFailed"; break;
                case "closed": job.Status = "closed"; break;
                case "draft-job": job.Status = "draft"; break;
                case "draft-rubric": config.RubricApprovalStatus = "draft"; break;
                case "empty-rubric": config.RubricJson = "[ \n ]"; break;
                case "invalid-rubric": config.RubricJson = "not json"; break;
                case "object-rubric": config.RubricJson = "{}"; break;
                case "missing-config": job.CurrentConfigVersionId = null; break;
                case "foreign-config":
                    var other = new Job { Id = "other", JobCode = "other" };
                    setup.Jobs.Add(other);
                    config.JobId = other.Id;
                    break;
                case "no-runs": config.ScoringRunCount = 0; break;
                case "no-prompt": setup.ScoringPrompts.Remove(prompt); break;
                case "draft-prompt": prompt.Status = "active"; break;
                case "empty-prompt": prompt.PromptText = " \n "; break;
                case "no-document": setup.ApplicationDocuments.RemoveRange(setup.ApplicationDocuments); break;
                case "missing-content": setup.DocumentBlobs.RemoveRange(setup.DocumentBlobs); break;
                case "empty-content": (await setup.DocumentBlobs.SingleAsync()).Content = " "; break;
                case "owned":
                    await setup.Applications.ExecuteUpdateAsync(setters =>
                        setters.SetProperty(row => row.ScoringOwner, "existing"));
                    break;
            }
            await setup.SaveChangesAsync();
        }
        await using var context = database.Open();
        (await new SequentialScoringQueueRepository(context).TryClaimAsync("owner", DateTime.UtcNow, Lease))
            .Should().BeNull();
        (await context.Applications.SingleAsync(row => row.Id == id)).ScoringOwner
            .Should().Be(exclusion == "owned" ? "existing" : null);
    }

    [Fact]
    public async Task Claim_RechecksEligibilityAndConfigurationAfterReadingCandidate()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync();
        var interceptor = new BeforeClaimWrite(async () =>
        {
            await using var competing = database.Open();
            await competing.JobConfigVersions.ExecuteUpdateAsync(setters =>
                setters.SetProperty(config => config.RubricApprovalStatus, "draft"));
        });
        await using var context = database.Open(interceptor);
        (await new SequentialScoringQueueRepository(context).TryClaimAsync("owner", DateTime.UtcNow, Lease))
            .Should().BeNull();
        var application = await context.Applications.AsNoTracking().SingleAsync();
        application.Status.Should().Be("Queued");
        application.ScoringOwner.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_AcceptsBlobReferencesAndSkipsPagesOfInvalidRubrics(bool useRubricV2)
    {
        await using var database = await TestDatabase.CreateAsync();
        for (var index = 0; index < 33; index++)
            await database.SeedAsync(createdAt: DateTime.UtcNow.AddHours(-1), rubric: "[]");
        var id = await database.SeedAsync(blobUri: "https://storage.invalid/document",
            rubric: useRubricV2 ? RubricV2 : null);
        await using var context = database.Open();
        var work = await new SequentialScoringQueueRepository(context).TryClaimAsync("owner", DateTime.UtcNow, Lease);
        work.Should().NotBeNull();
        work!.ApplicationId.Should().Be(id);
    }

    [Fact]
    public async Task RenewalPreservesLiveWorkAndRecoveryFailsOnlyExpiredOwnedScoring()
    {
        await using var database = await TestDatabase.CreateAsync();
        var expiredId = await database.SeedAsync(createdAt: DateTime.UtcNow.AddHours(-2));
        var liveId = await database.SeedAsync(createdAt: DateTime.UtcNow.AddHours(-1));
        var unownedId = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        var now = DateTime.UtcNow;
        await queue.TryClaimAsync("expired", now, Lease);
        await queue.TryClaimAsync("live", now, Lease);
        await context.Applications.Where(row => row.Id == unownedId).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Status, "Scoring")
            .SetProperty(row => row.ScoringLeaseUntil, now.AddMinutes(-1)));

        (await queue.RenewLeaseAsync(liveId, "wrong", now.AddMinutes(1), Lease)).Should().BeFalse();
        (await queue.RenewLeaseAsync(liveId, "live", now.AddMinutes(4), Lease)).Should().BeTrue();
        (await queue.RenewLeaseAsync(liveId, "live", now.AddMinutes(2), Lease)).Should().BeTrue();
        (await queue.RenewLeaseAsync(expiredId, "expired", now.AddMinutes(5), Lease)).Should().BeFalse();
        await queue.RecoverExpiredAsync(now.AddMinutes(6));
        await queue.RecoverExpiredAsync(now.AddMinutes(6));

        var applications = await context.Applications.AsNoTracking().ToDictionaryAsync(row => row.Id);
        applications[expiredId].Status.Should().Be("ScoringFailed");
        applications[expiredId].ScoringOwner.Should().BeNull();
        applications[expiredId].ScoringLeaseUntil.Should().BeNull();
        applications[expiredId].LastError.Should().Contain("outcome is unknown");
        applications[liveId].ScoringOwner.Should().Be("live");
        applications[liveId].ScoringLeaseUntil.Should().Be(now.AddMinutes(9));
        applications[unownedId].Status.Should().Be("Scoring");
        (await context.FailureQueueItems.SingleAsync()).EntityId.Should().Be(expiredId);
        (await queue.TryClaimAsync("new-owner", now.AddMinutes(6), Lease)).Should().BeNull();
    }

    [Fact]
    public async Task Finalize_ReplaysRolledBackDeadlockedCommitsWithoutDuplicatingResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using (var claiming = database.Open())
            await new SequentialScoringQueueRepository(claiming).TryClaimAsync("owner", DateTime.UtcNow, Lease);
        var commits = new CommitFailures(1205, 2);
        await using var context = database.OpenWithDeadlockRetries(commits);
        var prepared = Finalization(id, "Candidate") with
        {
            Runs = Enumerable.Range(1, 3).Select(index =>
                new ScoringRun { ApplicationId = id, RunIndex = index, TotalScore = 82 }).ToArray()
        };

        (await new SequentialScoringQueueRepository(context).FinalizeAsync(id, "owner", prepared)).Should().BeTrue();

        commits.Attempts.Should().Be(3);
        var application = await context.Applications.AsNoTracking().SingleAsync();
        application.Status.Should().Be("Completed");
        application.CandidateName.Should().Be("Candidate");
        application.ScoringOwner.Should().BeNull();
        (await context.ScoringRuns.AsNoTracking().Select(run => run.Id).ToListAsync())
            .Should().BeEquivalentTo(prepared.Runs.Select(run => run.Id));
        (await context.AggregatedResults.AsNoTracking().SingleAsync()).Id.Should().Be(prepared.Aggregate.Id);
    }

    [Theory]
    [InlineData(1205, 7)]
    [InlineData(2601, 1)]
    public async Task Finalize_PropagatesExhaustedOrPermanentSqlFailuresWithoutPartialWrites(int error, int attempts)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using (var claiming = database.Open())
            await new SequentialScoringQueueRepository(claiming).TryClaimAsync("owner", DateTime.UtcNow, Lease);
        var commits = new CommitFailures(error, 10);
        await using var context = database.OpenWithDeadlockRetries(commits);
        Func<Task> finalize = () => new SequentialScoringQueueRepository(context).FinalizeAsync(id, "owner", Finalization(id));

        if (error == 1205)
            await finalize.Should().ThrowAsync<RetryLimitExceededException>();
        else
            await finalize.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();

        commits.Attempts.Should().Be(attempts);
        context.ChangeTracker.Entries().Should().BeEmpty();
        var application = await context.Applications.AsNoTracking().SingleAsync();
        application.Status.Should().Be("Scoring");
        application.ScoringOwner.Should().Be("owner");
        (await context.ScoringRuns.CountAsync()).Should().Be(0);
        (await context.AggregatedResults.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Finalize_CancellationStopsDeadlockBackoffAndLeavesNoResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using (var claiming = database.Open())
            await new SequentialScoringQueueRepository(claiming).TryClaimAsync("owner", DateTime.UtcNow, Lease);
        using var cancellation = new CancellationTokenSource();
        var commits = new CommitFailures(1205, 10, cancellation);
        await using var context = database.OpenWithDeadlockRetries(commits);
        Func<Task> finalize = () => new SequentialScoringQueueRepository(context)
            .FinalizeAsync(id, "owner", Finalization(id), cancellation.Token);

        await finalize.Should().ThrowAsync<OperationCanceledException>();

        commits.Attempts.Should().Be(1);
        (await context.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Scoring");
        (await context.ScoringRuns.CountAsync()).Should().Be(0);
        (await context.AggregatedResults.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Finalize_UpdatesExistingAggregateWithoutReplacingCandidateWithBlankName(string? candidateName)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        await context.Applications.ExecuteUpdateAsync(setters => setters.SetProperty(app => app.CandidateName, "Original candidate"));
        var original = new AggregatedResult { ApplicationId = id, FinalScore = 10, Decision = "Excluded" };
        context.AggregatedResults.Add(original);
        await context.SaveChangesAsync();
        var queue = new SequentialScoringQueueRepository(context);
        await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);

        (await queue.FinalizeAsync(id, "owner", Finalization(id, candidateName))).Should().BeTrue();

        var aggregate = await context.AggregatedResults.AsNoTracking().SingleAsync();
        aggregate.Id.Should().Be(original.Id);
        aggregate.CreatedAt.Should().Be(original.CreatedAt);
        aggregate.FinalScore.Should().Be(82);
        aggregate.Decision.Should().Be("Eligible");
        (await context.Applications.AsNoTracking().SingleAsync()).CandidateName.Should().Be("Original candidate");
    }

    [Fact]
    public async Task Finalize_RechecksOwnershipAfterDeadlockBeforePublishingResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using (var claiming = database.Open())
            await new SequentialScoringQueueRepository(claiming).TryClaimAsync("owner", DateTime.UtcNow, Lease);
        var reads = new BeforeAggregateRead(async (attempt, ct) =>
        {
            if (attempt != 2)
                return;
            await using var competing = database.Open();
            await new SequentialScoringQueueRepository(competing).FailAsync(id, "owner", "ownership revoked", 0, ct);
        });
        var commits = new CommitFailures(1205, 1);
        await using var context = database.OpenWithDeadlockRetries(commits, reads);

        (await new SequentialScoringQueueRepository(context).FinalizeAsync(id, "owner", Finalization(id))).Should().BeFalse();

        var application = await context.Applications.AsNoTracking().SingleAsync();
        application.Status.Should().Be("ScoringFailed");
        application.ScoringOwner.Should().BeNull();
        (await context.FailureQueueItems.CountAsync()).Should().Be(1);
        (await context.ScoringRuns.CountAsync()).Should().Be(0);
        (await context.AggregatedResults.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task Finalize_BatchesResultPersistenceAndCombinesTerminalTransitionWithRelease(int runCount)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using (var claiming = database.Open())
            await new SequentialScoringQueueRepository(claiming).TryClaimAsync("owner", DateTime.UtcNow, Lease);
        var saves = new FinalizationSaves();
        var commands = new FinalizationCommands();
        await using var context = database.Open(saves, commands);
        var repository = new ApplicationRepository(context);
        var finalizer = new ApplicationScoringFinalizer(repository);
        var runs = Enumerable.Range(1, runCount)
            .Select(index => new ScoringRun { ApplicationId = id, RunIndex = index, TotalScore = 82 })
            .ToArray();

        var prepared = finalizer.Prepare(id, runs, runCount, 12, 65, "Scored candidate");
        (await new SequentialScoringQueueRepository(context).FinalizeAsync(id, "owner", prepared)).Should().BeTrue();

        saves.Count.Should().Be(1, "all runs and the aggregate should be saved in one batch");
        commands.ApplicationUpdates.Should().Be(1, "the ownership fence, terminal update and release share one write");
        commands.AggregateReads.Should().Be(1);
        commands.AggregateReadInsideTransaction.Should().BeFalse();
        var saved = await context.Applications.AsNoTracking().SingleAsync();
        saved.CandidateName.Should().Be("Scored candidate");
        saved.Status.Should().Be("Completed");
        saved.ScoringOwner.Should().BeNull();
        (await context.ScoringRuns.CountAsync()).Should().Be(runCount);
        (await context.AggregatedResults.SingleAsync()).FinalScore.Should().Be(82);
    }

    [Fact]
    public async Task Finalize_FencesWrongExpiredAndAlreadyReleasedOwners()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);
        var prepared = Finalization(id);
        (await queue.FinalizeAsync(id, "wrong", prepared)).Should().BeFalse();
        (await context.ScoringRuns.CountAsync()).Should().Be(0);
        context.ChangeTracker.Entries().Should().BeEmpty();
        (await queue.FinalizeAsync(id, "owner", prepared)).Should().BeTrue();
        (await queue.FinalizeAsync(id, "owner", prepared)).Should().BeFalse();
        (await context.ScoringRuns.CountAsync()).Should().Be(1);
        var saved = await context.Applications.AsNoTracking().SingleAsync();
        saved.Status.Should().Be("Completed");
        saved.FinalScore.Should().Be(82);
        saved.ScoringOwner.Should().BeNull();
        saved.ScoringLeaseUntil.Should().BeNull();
        await context.Applications.ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Status, "Scoring")
            .SetProperty(row => row.ScoringOwner, "expired")
            .SetProperty(row => row.ScoringLeaseUntil, DateTime.UtcNow.AddMinutes(-1)));
        (await queue.FinalizeAsync(id, "expired", prepared)).Should().BeFalse();
        (await context.ScoringRuns.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Finalize_RollsBackTerminalTransitionAndAllResultsWhenBatchFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);

        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_aggregate BEFORE INSERT ON AggregatedResults
            BEGIN SELECT RAISE(ABORT, 'simulated result persistence failure'); END;
            """);
        var prepared = Finalization(id, candidateName: "Candidate");
        var failure = () => queue.FinalizeAsync(id, "owner", prepared);
        await failure.Should().ThrowAsync<DbUpdateException>();
        var application = await context.Applications.AsNoTracking().SingleAsync();
        application.Status.Should().Be("Scoring");
        application.ScoringOwner.Should().Be("owner");
        application.CandidateName.Should().BeNull();
        application.FinalScore.Should().BeNull();
        (await context.ScoringRuns.CountAsync()).Should().Be(0);
        (await context.AggregatedResults.CountAsync()).Should().Be(0);
        context.ChangeTracker.Entries().Should().BeEmpty();
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_aggregate;");
        (await queue.FinalizeAsync(id, "owner", prepared)).Should().BeTrue();
        (await context.ScoringRuns.SingleAsync()).Id.Should().Be(prepared.Runs[0].Id);
    }

    [Fact]
    public async Task Fail_IsOwnerFencedAndInsertsExactlyOneDlqEntry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);
        await queue.FailAsync(id, "stale", "ignored", 1);
        (await context.FailureQueueItems.CountAsync()).Should().Be(0);
        await queue.FailAsync(id, "owner", "provider exhausted", 10);
        await queue.FailAsync(id, "owner", "duplicate", 10);
        var saved = await context.Applications.AsNoTracking().SingleAsync();
        saved.Status.Should().Be("ScoringFailed");
        saved.LastError.Should().Be("provider exhausted");
        saved.ScoringOwner.Should().BeNull();
        saved.ScoringLeaseUntil.Should().BeNull();
        var failure = await context.FailureQueueItems.SingleAsync();
        failure.EntityType.Should().Be("Application");
        failure.EntityId.Should().Be(id);
        failure.FailureReason.Should().Be("provider exhausted");
        failure.RetryCount.Should().Be(10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailurePersistenceRollsBackStatusWhenDlqInsertFails(bool recover)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        var now = DateTime.UtcNow;
        await queue.TryClaimAsync("owner", now, Lease);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_failure BEFORE INSERT ON FailureQueueItems
            BEGIN SELECT RAISE(ABORT, 'simulated DLQ failure'); END;
            """);
        Func<Task> action = recover
            ? () => queue.RecoverExpiredAsync(now + Lease)
            : () => queue.FailAsync(id, "owner", "failure", 10);
        await action.Should().ThrowAsync<DbUpdateException>();
        var saved = await context.Applications.AsNoTracking().SingleAsync();
        saved.Status.Should().Be("Scoring");
        saved.ScoringOwner.Should().Be("owner");
        (await context.FailureQueueItems.CountAsync()).Should().Be(0);
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_failure;");
        await action();
        (await context.FailureQueueItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task WholeEntityUpdatesCannotRewindHeartbeatOrOverwriteLaterOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        var now = DateTime.UtcNow;
        var unclaimed = await context.Applications.AsNoTracking().SingleAsync();
        await queue.TryClaimAsync("owner", now, Lease);
        context.Applications.Update(unclaimed);
        Func<Task> staleUpdate = () => context.SaveChangesAsync();
        await staleUpdate.Should().ThrowAsync<DbUpdateConcurrencyException>();
        context.ChangeTracker.Clear();

        var claimed = await context.Applications.AsNoTracking().SingleAsync();
        await queue.RenewLeaseAsync(id, "owner", now.AddMinutes(4), Lease);
        claimed.CandidateName = "Candidate";
        context.Applications.Update(claimed);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        (await context.Applications.AsNoTracking().SingleAsync()).ScoringLeaseUntil.Should().Be(now.AddMinutes(9));

        await queue.FailAsync(id, "owner", "failed", 10);
        await context.Applications.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "Queued"));
        await queue.TryClaimAsync("later-owner", now.AddMinutes(1), Lease);
        context.Applications.Update(claimed);
        await staleUpdate.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Finalize_UsesExistingFinalizerAndRejectsIncompleteOrMismatchedResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        var work = await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);
        Func<Task> incomplete = () => queue.FinalizeAsync(id, "owner", Finalization(id) with { Status = "Scoring" });
        await incomplete.Should().ThrowAsync<ArgumentException>();
        Func<Task> mismatched = () => queue.FinalizeAsync(id, "owner", Finalization("different-application"));
        await mismatched.Should().ThrowAsync<ArgumentException>();
        var wrongRun = Finalization(id) with { Runs = [new ScoringRun { ApplicationId = "different-application" }] };
        Func<Task> mismatchedRun = () => queue.FinalizeAsync(id, "owner", wrongRun);
        await mismatchedRun.Should().ThrowAsync<ArgumentException>();
        (await context.Applications.AsNoTracking().SingleAsync()).ScoringOwner.Should().Be("owner");

        var repository = new ApplicationRepository(context);
        var finalizer = new ApplicationScoringFinalizer(repository);
        var run = new ScoringRun { ApplicationId = id, TotalScore = 82 };
        (await queue.FinalizeAsync(id, "owner", finalizer.Prepare(id, [run], 1, 12, 65))).Should().BeTrue();
        (await context.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Completed");
        (await context.AggregatedResults.SingleAsync()).FinalScore.Should().Be(82);
        (await context.ScoringRuns.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Finalize_LockPreventsRecoveryFromRevokingOwnershipBeforeResultsCommit()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        var now = DateTime.UtcNow;
        var startedRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? recovery = null;
        var afterWrite = new AfterApplicationWrite(async ct =>
        {
            recovery = Task.Run(async () =>
            {
                await using var competing = database.Open();
                startedRecovery.SetResult();
                await new SequentialScoringQueueRepository(competing).RecoverExpiredAsync(now.AddHours(1));
            });
            await startedRecovery.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        });
        await using (var claiming = database.Open())
            await new SequentialScoringQueueRepository(claiming).TryClaimAsync("owner", now, Lease);
        await using var context = database.Open(afterWrite);
        var queue = new SequentialScoringQueueRepository(context);
        (await queue.FinalizeAsync(id, "owner", Finalization(id))).Should().BeTrue();
        await recovery!;
        (await context.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Completed");
        (await context.FailureQueueItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Retry_IsAtomicExplicitAndCannotResetOwnedOrCompletedWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        var now = DateTime.UtcNow;
        await queue.TryClaimAsync("owner", now, Lease);
        await queue.FailAsync(id, "owner", "provider failure", 10);
        var failureId = (await context.FailureQueueItems.SingleAsync()).Id;
        (await queue.RetryAsync(failureId)).Should().BeTrue();
        (await queue.RetryAsync(failureId)).Should().BeFalse();
        var application = await context.Applications.AsNoTracking().SingleAsync();
        application.Status.Should().Be("Queued");
        application.LastError.Should().BeNull();
        (await queue.TryClaimAsync("next-owner", now, Lease))!.ApplicationId.Should().Be(id);
        var staleFailure = new FailureQueueItem { EntityId = id, EntityType = "Application" };
        context.FailureQueueItems.Add(staleFailure);
        await context.SaveChangesAsync();
        (await queue.RetryAsync(staleFailure.Id)).Should().BeFalse();
        (await context.Applications.AsNoTracking().SingleAsync()).ScoringOwner.Should().Be("next-owner");
        (await queue.FinalizeAsync(id, "next-owner", Finalization(id))).Should().BeTrue();
        (await queue.RetryAsync(staleFailure.Id)).Should().BeFalse();
    }

    [Theory]
    [InlineData("ScoringFailed", false, false, true)]
    [InlineData("ExtractionFailed", false, false, true)]
    [InlineData("ScoringFailed", true, false, false)]
    [InlineData("ExtractionFailed", true, false, false)]
    [InlineData("ScoringFailed", false, true, false)]
    [InlineData("ExtractionFailed", false, true, false)]
    [InlineData("Scoring", false, true, false)]
    [InlineData("Scoring", false, false, false)]
    [InlineData("Completed", false, false, false)]
    [InlineData("Queued", false, false, false)]
    [InlineData("Uploading", false, false, false)]
    public async Task Retry_OnlyRequeuesUnownedProductionFailures(
        string status, bool testRun, bool owned, bool expected)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var originalLease = DateTime.UtcNow.AddMinutes(5);
        await context.Applications.ExecuteUpdateAsync(setters => setters
            .SetProperty(application => application.Status, status)
            .SetProperty(application => application.TestRunId, testRun ? "test-run" : null)
            .SetProperty(application => application.ScoringOwner, owned ? "owner" : null)
            .SetProperty(application => application.ScoringLeaseUntil, originalLease)
            .SetProperty(application => application.LastError, "original failure"));
        var failure = new FailureQueueItem { EntityType = "Application", EntityId = id, FailureReason = "original failure" };
        context.FailureQueueItems.Add(failure);
        await context.SaveChangesAsync();

        (await new SequentialScoringQueueRepository(context).RetryAsync(failure.Id)).Should().Be(expected);

        var application = await context.Applications.AsNoTracking().SingleAsync();
        application.Status.Should().Be(expected ? "Queued" : status);
        application.LastError.Should().Be(expected ? null : "original failure");
        application.ScoringOwner.Should().Be(owned ? "owner" : null);
        application.ScoringLeaseUntil.Should().Be(expected ? null : originalLease);
        application.TestRunId.Should().Be(testRun ? "test-run" : null);
        (await context.FailureQueueItems.AnyAsync(item => item.Id == failure.Id)).Should().Be(!expected);
    }

    [Fact]
    public async Task Retry_MissingItemReturnsFalseWithoutChangingApplication()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);

        (await queue.RetryAsync("missing-item")).Should().BeFalse();

        (await context.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Queued");
        (await context.FailureQueueItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Retry_CompetingScopesOnlyPublishOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        string failureId;
        await using (var context = database.Open())
        {
            var queue = new SequentialScoringQueueRepository(context);
            await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);
            await queue.FailAsync(id, "owner", "failure", 10);
            failureId = (await context.FailureQueueItems.SingleAsync()).Id;
        }
        await using var first = database.Open();
        await using var second = database.Open();

        var results = await Task.WhenAll(
            Task.Run(() => new SequentialScoringQueueRepository(first).RetryAsync(failureId)),
            Task.Run(() => new SequentialScoringQueueRepository(second).RetryAsync(failureId)));

        results.Count(result => result).Should().Be(1);
        await using var verify = database.Open();
        (await verify.Applications.SingleAsync()).Status.Should().Be("Queued");
        (await verify.FailureQueueItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Retry_RollsBackWhenDlqDeletionFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);
        await queue.FailAsync(id, "owner", "provider failure", 10);
        var failureId = (await context.FailureQueueItems.SingleAsync()).Id;
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_retry BEFORE DELETE ON FailureQueueItems
            BEGIN SELECT RAISE(ABORT, 'simulated DLQ deletion failure'); END;
            """);
        Func<Task> retry = () => queue.RetryAsync(failureId);
        await retry.Should().ThrowAsync<SqliteException>();
        (await context.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("ScoringFailed");
        (await context.FailureQueueItems.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_UsesProviderSpecificLeaseColumnsAndSchema(bool sqlServer)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (sqlServer)
            options.UseSqlServer("Server=localhost;Database=unused;Integrated Security=True");
        else
            options.UseSqlite("Data Source=:memory:");
        using var context = new AppDbContext(options.Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20260909184257_AddExtractionInstructionLifecycle", "20260916191750_AddSequentialScoringLease");
        script.Should().Contain("ScoringOwner").And.Contain("ScoringLeaseUntil");
        if (sqlServer)
            script.Should().Contain("[talentmatch].[Applications]").And.Contain("nvarchar(128)").And.Contain("datetime2");
        else
            script.Should().Contain("\"Applications\"").And.Contain("TEXT");
    }

    [Fact]
    public async Task SharedSqliteSchema_CreatesQueueColumnsAndIndexesIdempotently()
    {
        var schema = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "server", "storage", "schema-sqlite.sql"));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync();
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT ScoringOwner, ScoringLeaseUntil FROM Applications;";
        await using (var reader = await command.ExecuteReaderAsync())
            reader.FieldCount.Should().Be(2);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('IX_Applications_Status_ScoringLeaseUntil', 'IX_Applications_Status_TestRunId_CreatedAt');";
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Migration_UpgradesLegacyApplicationsWithoutChangingLiveState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE Applications (Id TEXT PRIMARY KEY, Status TEXT NOT NULL, TestRunId TEXT NULL, CreatedAt TEXT NOT NULL);
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO Applications VALUES ('legacy-live', 'Scoring', NULL, '2026-09-16 00:00:00');
            """);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20260909184257_AddExtractionInstructionLifecycle", "20260916191750_AddSequentialScoringLease");
        await context.Database.ExecuteSqlRawAsync(script);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status, ScoringOwner, ScoringLeaseUntil FROM Applications;";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Scoring");
        reader.IsDBNull(1).Should().BeTrue();
        reader.IsDBNull(2).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationRollsBackFinalizationAndNeverPublishesOrFailsWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.SeedAsync();
        using var duringFinalization = new CancellationTokenSource();
        var afterWrite = new AfterApplicationWrite(_ =>
        {
            duringFinalization.Cancel();
            return Task.CompletedTask;
        });
        await using var context = database.Open();
        var queue = new SequentialScoringQueueRepository(context);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> claim = () => queue.TryClaimAsync("owner", DateTime.UtcNow, Lease, cancellation.Token);
        await claim.Should().ThrowAsync<OperationCanceledException>();
        (await context.Applications.SingleAsync()).Status.Should().Be("Queued");

        await queue.TryClaimAsync("owner", DateTime.UtcNow, Lease);
        await using var finalizing = database.Open(afterWrite);
        Func<Task> finalize = () => new SequentialScoringQueueRepository(finalizing)
            .FinalizeAsync(id, "owner", Finalization(id), duringFinalization.Token);
        await finalize.Should().ThrowAsync<OperationCanceledException>();
        (await context.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Scoring");
        (await context.ScoringRuns.CountAsync()).Should().Be(0);
        (await context.AggregatedResults.CountAsync()).Should().Be(0);
        Func<Task> fail = () => queue.FailAsync(id, "owner", "cancelled", 0, cancellation.Token);
        await fail.Should().ThrowAsync<OperationCanceledException>();
        Func<Task> recover = () => queue.RecoverExpiredAsync(DateTime.UtcNow.AddHours(1), cancellation.Token);
        await recover.Should().ThrowAsync<OperationCanceledException>();
        (await context.FailureQueueItems.CountAsync()).Should().Be(0);
    }

    private static ApplicationScoringFinalization Finalization(string applicationId, string? candidateName = null) =>
        new([new ScoringRun { ApplicationId = applicationId, TotalScore = 82 }],
            new AggregatedResult { ApplicationId = applicationId, FinalScore = 82, Decision = "Eligible", Confidence = 1 },
            "Completed", candidateName);

    private static string FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null
               && (!File.Exists(Path.Combine(root.FullName, "package.json"))
                   || !File.Exists(Path.Combine(root.FullName, "server", "storage", "schema-sqlite.sql"))))
            root = root.Parent;
        return root?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class AfterApplicationWrite(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (result == 1 && command.CommandText.StartsWith("UPDATE \"Applications\"", StringComparison.Ordinal))
                await action(cancellationToken);
            return result;
        }
    }

    private sealed class CommitFailures(int number, int count, CancellationTokenSource? cancellation = null) : DbTransactionInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= count)
            {
                cancellation?.Cancel();
                throw SqlServerTestErrors.Create(number);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BeforeAggregateRead(Func<int, CancellationToken, Task> action) : DbCommandInterceptor
    {
        private int _reads;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"AggregatedResults\"", StringComparison.Ordinal))
                await action(++_reads, cancellationToken);
            return result;
        }
    }

    private sealed class DeadlockStrategyFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new SequentialScoringExecutionStrategy(dependencies.CurrentContext.Context);
    }

    private sealed class FinalizationSaves : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FinalizationCommands : DbCommandInterceptor
    {
        public int ApplicationUpdates { get; private set; }
        public int AggregateReads { get; private set; }
        public bool AggregateReadInsideTransaction { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"Applications\"", StringComparison.Ordinal))
                ApplicationUpdates++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"AggregatedResults\"", StringComparison.Ordinal))
            {
                AggregateReads++;
                AggregateReadInsideTransaction |= command.Transaction is not null;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ClaimBarrier : DbCommandInterceptor
    {
        private int _arrivals;
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                    _bothArrived.TrySetResult();
                await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }

    private sealed class BeforeClaimWrite(Func<Task> action) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
                await action();
            return result;
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Directory.GetCurrentDirectory(), $"sequential-scoring-{Guid.NewGuid():N}.db");

        public static async Task<TestDatabase> CreateAsync()
        {
            var database = new TestDatabase();
            await using var context = database.Open();
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        public AppDbContext Open(params IInterceptor[] interceptors) => new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_path};Pooling=False;Default Timeout=15")
                .AddInterceptors(interceptors).Options);

        public AppDbContext OpenWithDeadlockRetries(params IInterceptor[] interceptors) => new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_path};Pooling=False;Default Timeout=15")
                .ReplaceService<IExecutionStrategyFactory, DeadlockStrategyFactory>()
                .AddInterceptors(interceptors).Options);

        public async Task<string> SeedAsync(DateTime? createdAt = null, string? rubric = null, string? blobUri = null)
        {
            await using var context = Open();
            var job = new Job { JobCode = Guid.NewGuid().ToString(), Status = "ACTIVE", JobDescription = "Approved job description" };
            var config = new JobConfigVersion
            {
                JobId = job.Id,
                RubricApprovalStatus = "approved",
                RubricJson = rubric ?? """[{"name":"Experience","weight":100}]""",
                ScoringRunCount = 3,
                VarianceThreshold = 12,
                LonglistThreshold = 65
            };
            job.CurrentConfigVersionId = config.Id;
            var application = new ApplicationEntity { JobId = job.Id, CreatedAt = createdAt ?? DateTime.UtcNow };
            var document = new ApplicationDocument { ApplicationId = application.Id, FileName = "cv.txt", BlobUri = blobUri };
            var prompt = new ScoringPrompt
            {
                JobId = job.Id,
                PromptText = "Score {{JOB_SPEC_TEXT}}",
                Status = "production-approved",
                ModelId = "o3",
                ReasoningLevel = "high",
                ApprovedModelId = "o3",
                ApprovedReasoningLevel = "high"
            };
            var testRun = new PromptTestRun
            {
                JobId = job.Id,
                PromptId = prompt.Id,
                Status = "approved",
                ModelId = "o3",
                ReasoningLevel = "high",
                ApprovedModelId = "o3",
                ApprovedReasoningLevel = "high",
                ApplicationIdsJson = "[]"
            };
            prompt.ApprovedTestRunId = testRun.Id;
            context.AddRange(job, config, application, document, prompt, testRun);
            if (blobUri is null)
                context.DocumentBlobs.Add(new DocumentBlob { DocumentId = document.Id, Content = "Y3Y=" });
            await context.SaveChangesAsync();
            return application.Id;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(_path + suffix);
            return ValueTask.CompletedTask;
        }
    }
}
