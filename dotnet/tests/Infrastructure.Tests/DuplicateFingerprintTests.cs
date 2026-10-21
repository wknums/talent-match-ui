using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.Common.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

// One duplicate standard across every Stack B upload path: a document is a duplicate whether the existing row holds
// the raw-byte hash (Stack B legacy, optional upload, bulk scoring) or the base64-text hash written by the older
// Stack A upload (T050).
public sealed class DuplicateFingerprintTests
{
    private static readonly byte[] Cv = "synthetic candidate cv"u8.ToArray();
    private static readonly string Raw = ContentFingerprint.Raw(Cv);
    private static readonly string Legacy = ContentFingerprint.LegacyBase64(Cv);

    [Fact]
    public void The_two_fingerprint_styles_differ_and_the_base64_helper_matches_the_byte_helper()
    {
        Raw.Should().NotBe(Legacy);
        ContentFingerprint.LegacyOfBase64(Convert.ToBase64String(Cv)).Should().Be(Legacy);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("legacy")]
    public async Task The_legacy_upload_skips_a_file_already_in_the_job_whichever_hash_the_row_holds(string existingStyle)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(existingStyle == "raw" ? Raw : Legacy);

        var created = await fixture.UploadAsync(allowDuplicates: false);

        created.Should().BeEmpty();
        (await fixture.Context.Applications.CountAsync()).Should().Be(1, "only the pre-existing application remains");
    }

    [Fact]
    public async Task The_legacy_upload_still_adds_a_file_that_is_new_to_the_job()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(ContentFingerprint.Raw("another file"u8));

        var created = await fixture.UploadAsync(allowDuplicates: false);

        created.Should().ContainSingle();
        (await fixture.Context.ApplicationDocuments.AsNoTracking().SingleAsync(d => d.ApplicationId == created[0].Id))
            .Fingerprint.Should().Be(Raw, "new rows keep the raw-byte standard");
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("legacy")]
    public async Task The_legacy_upload_imports_a_prompt_test_document_as_a_separate_production_application(string existingStyle)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(existingStyle == "raw" ? Raw : Legacy, testRunId: "test-run-1");

        var created = await fixture.UploadAsync(allowDuplicates: false);

        created.Should().ContainSingle().Which.TestRunId.Should().BeNull();
        var production = await new ApplicationRepository(fixture.Context).GetByJobIdAsync("job-1", null, 65, 80);
        production.Select(application => application.Id).Should().Equal(created[0].Id);
        (await fixture.Context.Applications.CountAsync()).Should().Be(2, "the prompt-test application is preserved");
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("legacy")]
    public async Task The_job_duplicate_lookup_ignores_prompt_tests_but_still_finds_production_documents(string existingStyle)
    {
        await using var fixture = await Fixture.CreateAsync();
        var fingerprint = existingStyle == "raw" ? Raw : Legacy;
        var repository = new ApplicationRepository(fixture.Context);
        await fixture.AddExistingDocumentAsync(fingerprint, testRunId: "test-run-1");

        (await repository.FindDocumentByFingerprintAsync("job-1", fingerprint)).Should().BeNull();

        await fixture.AddExistingDocumentAsync(fingerprint);

        var existing = await repository.FindDocumentByFingerprintAsync("job-1", fingerprint);
        existing.Should().NotBeNull();
        (await repository.GetByIdAsync(existing!.ApplicationId))!.TestRunId.Should().BeNull();
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("legacy")]
    public async Task Allow_duplicates_still_permits_the_same_document(string existingStyle)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(existingStyle == "raw" ? Raw : Legacy);

        var created = await fixture.UploadAsync(allowDuplicates: true);

        created.Should().ContainSingle();
        (await fixture.Context.Applications.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_duplicate_check_never_rewrites_the_existing_row()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(Legacy);

        await fixture.UploadAsync(allowDuplicates: false);

        (await fixture.Context.ApplicationDocuments.AsNoTracking().SingleAsync())
            .Fingerprint.Should().Be(Legacy, "the pre-change row is left exactly as it was");
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("legacy")]
    public async Task The_optional_upload_marks_a_file_already_in_the_job_as_a_duplicate_whichever_hash_the_row_holds(string existingStyle)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(existingStyle == "raw" ? Raw : Legacy);

        var item = await fixture.CompleteOptionalUploadAsync(allowDuplicates: false);

        item.Status.Should().Be(UploadItemStatus.SkippedDuplicate);
        item.OutcomeCode.Should().Be("duplicate_existing");
        (await fixture.Context.Applications.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("legacy")]
    public async Task The_optional_upload_with_allow_duplicates_still_imports_the_document(string existingStyle)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(existingStyle == "raw" ? Raw : Legacy);

        var item = await fixture.CompleteOptionalUploadAsync(allowDuplicates: true);

        item.Status.Should().Be(UploadItemStatus.Succeeded);
        (await fixture.Context.Applications.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("legacy")]
    public async Task The_optional_upload_imports_a_prompt_test_document_as_a_separate_production_application(string existingStyle)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddExistingDocumentAsync(existingStyle == "raw" ? Raw : Legacy, testRunId: "test-run-1");

        var item = await fixture.CompleteOptionalUploadAsync(allowDuplicates: false);

        item.Status.Should().Be(UploadItemStatus.Succeeded);
        var production = await new ApplicationRepository(fixture.Context).GetByJobIdAsync("job-1", null, 65, 80);
        production.Should().ContainSingle().Which.Id.Should().Be(item.ApplicationId);
        production[0].TestRunId.Should().BeNull();
        (await fixture.Context.Applications.CountAsync()).Should().Be(2, "the prompt-test application is preserved");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Context { get; }

        private Fixture(SqliteConnection connection, AppDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            context.Jobs.Add(new Job { Id = "job-1", Title = "Job" });
            await context.SaveChangesAsync();
            return new(connection, context);
        }

        public async Task AddExistingDocumentAsync(string fingerprint, string? testRunId = null)
        {
            var application = new TalentMatch.Domain.Entities.Application
            {
                JobId = "job-1",
                CandidateRef = "candidate-existing",
                CandidateName = "Existing",
                Status = "Queued",
                TestRunId = testRunId,
            };
            Context.Applications.Add(application);
            Context.ApplicationDocuments.Add(new ApplicationDocument
            {
                ApplicationId = application.Id,
                FileName = "existing.md",
                FileType = "text/markdown",
                FileSize = Cv.Length,
                Fingerprint = fingerprint,
                ContentBase64 = Convert.ToBase64String(Cv),
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public Task<List<TalentMatch.Domain.Entities.Application>> UploadAsync(bool allowDuplicates)
        {
            var handler = new UploadApplicationsCommandHandler(new ApplicationRepository(Context), new JobRepository(Context));
            var file = new UploadedFile("cv.md", "text/markdown", Cv.Length, Convert.ToBase64String(Cv), Raw);
            return handler.Handle(new UploadApplicationsCommand("job-1", [file], allowDuplicates), CancellationToken.None);
        }

        public async Task<UploadItem> CompleteOptionalUploadAsync(bool allowDuplicates)
        {
            var repository = new UploadSessionRepository(Context);
            var session = new UploadSession
            {
                Id = Guid.NewGuid().ToString(),
                JobId = "job-1",
                OwnerActorId = "owner-1",
                AllowDuplicates = allowDuplicates,
                FileConcurrency = 4,
                MaxIndividualFileBytes = 8_388_608,
                MaxInFlightBytes = 104_857_600,
            };
            var occurrence = Guid.NewGuid().ToString();
            var item = new UploadItem
            {
                Id = "item-1",
                SessionId = session.Id,
                OccurrenceKey = occurrence,
                Ordinal = 0,
                FileName = "cv.md",
                MimeType = "text/markdown",
                RawSizeBytes = Cv.Length,
            };
            await repository.CreateAsync(
                session,
                [item],
                new ProcessingEvent { EventType = "upload-session.created", EntityType = nameof(UploadSession), EntityId = session.Id, Actor = "owner-1" },
                0);
            var detached = await repository.GetItemAsync(session.Id, item.Id, "owner-1");
            detached!.TransitionTo(UploadItemStatus.Uploading, DateTime.UtcNow);
            await repository.UpdateItemAsync(detached, 1, [], CancellationToken.None);

            return await repository.CompleteItemAsync(
                session.Id, item.Id, "owner-1", occurrence, Raw, "cv.md", "text/markdown", Cv.Length,
                Convert.ToBase64String(Cv), CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
