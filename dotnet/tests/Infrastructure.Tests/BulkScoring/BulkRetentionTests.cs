using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.SharePoint;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringRepositoryTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

// Existing job deletion removed only database rows. Bulk scoring adds files outside the database
// (staged and retained originals), so deleting a job must remove those too (FR-021).
public sealed class BulkRetentionTests
{
    [Fact]
    public async Task Deleting_a_job_removes_its_requests_rows_and_files_but_not_another_jobs_and_keeps_the_audit_trail()
    {
        await using var f = await Fixture.CreateAsync(migrate: false);
        f.Db.Jobs.Add(new Job { Id = "job-2", Title = "Other" });
        await f.Db.SaveChangesAsync();
        var blobs = new RecordingBlobStore();
        var content = new BulkContentStore(blobs);

        var doomed = await f.ActiveAsync("a", 2, owningStack: "B", withAuthorization: true);
        var kept = await f.ActiveAsync("b", 1, owningStack: "B");
        await f.Db.BulkScoringRequests.Where(r => r.Id == kept.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, "job-2"));
        foreach (var request in new[] { doomed, kept })
            foreach (var item in await f.Db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == request.Id).ToListAsync())
                await f.Repo.AddContentAsync(await content.StageAsync(request.Id, item.Id, BulkContentKind.Original, item.FileName, "application/pdf", [1, 2, 3]));
        f.Db.ProcessingEvents.Add(new ProcessingEvent { Actor = "user", EventType = "bulk-scoring-request.accepted", EntityType = nameof(BulkScoringRequest), EntityId = doomed.Id });
        await f.Db.SaveChangesAsync();
        blobs.Names.Should().HaveCount(3);

        var handler = new DeleteJobCommandHandler(new JobRepository(f.Db), new AdminUser(), null, f.Repo, content);
        (await handler.Handle(new DeleteJobCommand("job-1"), CancellationToken.None)).Should().BeTrue();

        f.Db.ChangeTracker.Clear();
        (await f.Db.Jobs.AnyAsync(j => j.Id == "job-1")).Should().BeFalse();
        (await f.Db.BulkScoringRequests.Select(r => r.Id).ToListAsync()).Should().Equal(kept.Id);
        (await f.Db.BulkScoringItems.CountAsync(i => i.RequestId == doomed.Id)).Should().Be(0);
        (await f.Db.BulkScoringItemContents.CountAsync()).Should().Be(1, "only the other job's file row remains");
        (await f.Db.BulkRequestAuthorizations.CountAsync(a => a.RequestId == doomed.Id)).Should().Be(0);
        blobs.Names.Should().HaveCount(1).And.OnlyContain(n => n.StartsWith($"bulk/{kept.Id}/"), "nothing of the deleted job is orphaned");
        (await f.Db.ProcessingEvents.CountAsync(e => e.EntityId == doomed.Id)).Should().Be(1, "audit entries outlive the request");
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_blocks_completion_but_does_not_stop_other_content_cleanup()
    {
        await using var f = await Fixture.CreateAsync(migrate: false);
        var blobs = new RecordingBlobStore();
        var content = new BulkContentStore(blobs);
        var first = await f.ActiveAsync("a", 1, owningStack: "B");
        var second = await f.ActiveAsync("b", 1, owningStack: "B");
        foreach (var request in new[] { first, second })
            foreach (var item in await f.Db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == request.Id).ToListAsync())
                await f.Repo.AddContentAsync(await content.StageAsync(request.Id, item.Id, BulkContentKind.Original, item.FileName, "application/pdf", [9]));
        blobs.FailPrefix = $"bulk/{first.Id}/";

        var handler = new DeleteJobCommandHandler(new JobRepository(f.Db), new AdminUser(), null, f.Repo, content);
        (await handler.Handle(new DeleteJobCommand("job-1"), CancellationToken.None)).Should().BeFalse(
            "FR-021 forbids reporting completed deletion while known staged content remains");

        (await f.Db.Jobs.AnyAsync(j => j.Id == "job-1")).Should().BeTrue(
            "the fenced job remains available for cleanup reconciliation");
        blobs.Names.Should().OnlyContain(n => n.StartsWith($"bulk/{first.Id}/"), "only the file that failed to delete is left, and it is logged");
    }

    [Fact]
    public async Task Expired_previews_disappear_without_touching_active_requests()
    {
        await using var f = await Fixture.CreateAsync(migrate: false);
        var active = await f.ActiveAsync("a", 2, owningStack: "B");
        var expired = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(1, "x"), expires: Now.AddMinutes(-1)));
        var live = await f.Repo.CreateDraftAsync(f.NewRequest(), Step(Items(1, "y"), expires: Now.AddMinutes(20)));

        var purged = await f.Repo.PurgeExpiredDraftsAsync(Now);

        purged.Should().Equal(expired.Id);
        f.Db.ChangeTracker.Clear();
        (await f.Db.BulkScoringRequests.Select(r => r.Id).ToListAsync()).Should().BeEquivalentTo([active.Id, live.Id]);
        (await f.Db.BulkScoringItems.CountAsync(i => i.RequestId == expired.Id)).Should().Be(0);
        (await f.Db.BulkScoringItems.CountAsync(i => i.RequestId == active.Id)).Should().Be(2);
    }

    private sealed class AdminUser : ICurrentUserService
    {
        public string? UserId => "admin";
        public string? Username => "admin";
        public string? Role => "admin";
        public string? Department => null;
        public bool IsAdmin => true;
        public Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<CurrentAuthorizationState?>(null);
    }

    private sealed class RecordingBlobStore : IBlobStore
    {
        private readonly Dictionary<string, byte[]> _blobs = [];
        public string? FailPrefix { get; set; }
        public IReadOnlyCollection<string> Names => _blobs.Keys.ToArray();
        public bool IsRemote => true;

        public Task<BlobUploadResult> PutAsync(string jobId, string applicationId, string documentId, string fileName, string mimeType, byte[] bytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> ReadByUriAsync(string blobUri, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.GetValueOrDefault(blobUri.Replace("https://fake/", "")));

        public Task<BlobUploadResult> PutObjectAsync(string blobName, string mimeType, byte[] bytes, CancellationToken cancellationToken = default)
        {
            _blobs[blobName] = bytes;
            return Task.FromResult(new BlobUploadResult("https://fake/" + blobName, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
        }

        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            if (prefix == FailPrefix) throw new IOException("storage unavailable");
            foreach (var key in _blobs.Keys.Where(k => k.StartsWith(prefix)).ToArray()) _blobs.Remove(key);
            return Task.CompletedTask;
        }
    }
}
