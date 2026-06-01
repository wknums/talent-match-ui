using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public class ApplicationRepositoryTests
{
    [Fact]
    public async Task GetByIdAsync_ReturnsApplication_WhenRelatedTablesAreUnavailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = """
                CREATE TABLE Applications (
                    Id TEXT NOT NULL PRIMARY KEY,
                    JobId TEXT NOT NULL,
                    CandidateRef TEXT NOT NULL DEFAULT '',
                    CandidateName TEXT NULL,
                    CandidateEmail TEXT NULL,
                    Status TEXT NOT NULL,
                    FinalScore REAL NULL,
                    FinalDecision TEXT NULL,
                    Variance REAL NULL,
                    Flagged INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    TestRunId TEXT NULL,
                    LastError TEXT NULL
                );
                """;
            await setupCommand.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new AppDbContext(options);
        var repository = new ApplicationRepository(context);

        var application = new TalentMatch.Domain.Entities.Application
        {
            JobId = "job-1",
            Status = "Queued"
        };

        await repository.AddAsync(application);

        var loaded = await repository.GetByIdAsync(application.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(application.Id);
        loaded.JobId.Should().Be("job-1");
    }

    [Fact]
    public async Task AddDocumentAsync_SetsUploadTimestamp_WhenMissing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = """
                CREATE TABLE Applications (
                    Id TEXT NOT NULL PRIMARY KEY,
                    JobId TEXT NOT NULL,
                    CandidateRef TEXT NOT NULL DEFAULT '',
                    CandidateName TEXT NULL,
                    CandidateEmail TEXT NULL,
                    Status TEXT NOT NULL,
                    FinalScore REAL NULL,
                    FinalDecision TEXT NULL,
                    Variance REAL NULL,
                    Flagged INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    TestRunId TEXT NULL,
                    LastError TEXT NULL
                );

                CREATE TABLE ApplicationDocuments (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ApplicationId TEXT NOT NULL,
                    FileName TEXT NOT NULL DEFAULT '',
                    FileType TEXT NOT NULL,
                    FileSize INTEGER NOT NULL DEFAULT 0,
                    UploadTimestamp TEXT NOT NULL,
                    MimeType TEXT NOT NULL DEFAULT '',
                    SizeBytes INTEGER NOT NULL DEFAULT 0,
                    Fingerprint TEXT NOT NULL DEFAULT '',
                    UploadedAt TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE DocumentBlobs (
                    DocumentId TEXT NOT NULL PRIMARY KEY,
                    Content TEXT NOT NULL
                );
                """;
            await setupCommand.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new AppDbContext(options);
        var repository = new ApplicationRepository(context);

        var application = new TalentMatch.Domain.Entities.Application
        {
            JobId = "job-1",
            Status = "Queued"
        };

        await repository.AddAsync(application);

        var document = new ApplicationDocument
        {
            ApplicationId = application.Id,
            FileName = "resume.txt",
            FileType = "text/plain",
            FileSize = 14,
            Fingerprint = "fingerprint",
            ContentBase64 = Convert.ToBase64String("hello test run"u8.ToArray())
        };

        await repository.AddDocumentAsync(document);

        document.UploadTimestamp.Should().NotBeNull();

        await using var verifyCommand = connection.CreateCommand();
        verifyCommand.CommandText = "SELECT UploadedAt, FileType, UploadTimestamp FROM ApplicationDocuments WHERE Id = $id";
        verifyCommand.Parameters.AddWithValue("$id", document.Id);

        await using var reader = await verifyCommand.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetString(0).Should().NotBeNullOrWhiteSpace();
        reader.GetString(1).Should().Be("text/plain");
        reader.GetString(2).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SetExtractionAsync_PopulatesLegacyAndNewColumns_WhenNormalisedTextIsRequired()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = """
                CREATE TABLE Applications (
                    Id TEXT NOT NULL PRIMARY KEY,
                    JobId TEXT NOT NULL,
                    CandidateRef TEXT NOT NULL DEFAULT '',
                    CandidateName TEXT NULL,
                    CandidateEmail TEXT NULL,
                    Status TEXT NOT NULL,
                    FinalScore REAL NULL,
                    FinalDecision TEXT NULL,
                    Variance REAL NULL,
                    Flagged INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    TestRunId TEXT NULL,
                    LastError TEXT NULL
                );

                CREATE TABLE ExtractionArtifacts (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ApplicationId TEXT NOT NULL,
                    NormalisedText TEXT NOT NULL,
                    Markdown TEXT NOT NULL DEFAULT '',
                    ConfidenceScore REAL NOT NULL DEFAULT 0,
                    Confidence REAL NOT NULL DEFAULT 0,
                    Status TEXT NOT NULL DEFAULT 'pending',
                    CreatedAt TEXT NOT NULL
                );
                """;
            await setupCommand.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new AppDbContext(options);
        var repository = new ApplicationRepository(context);

        var application = new TalentMatch.Domain.Entities.Application
        {
            JobId = "job-1",
            Status = "Queued"
        };
        await repository.AddAsync(application);

        var extraction = new ExtractionArtifact
        {
            ApplicationId = application.Id,
            NormalisedText = "extracted markdown",
            ConfidenceScore = 0.92,
            Status = "completed"
        };

        await repository.SetExtractionAsync(extraction);

        await using var verifyCommand = connection.CreateCommand();
        verifyCommand.CommandText = "SELECT NormalisedText, Markdown, ConfidenceScore, Confidence FROM ExtractionArtifacts WHERE ApplicationId = $applicationId";
        verifyCommand.Parameters.AddWithValue("$applicationId", application.Id);

        await using var reader = await verifyCommand.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("extracted markdown");
        reader.GetString(1).Should().Be("extracted markdown");
        reader.GetDouble(2).Should().Be(0.92);
        reader.GetDouble(3).Should().Be(0.92);
    }

    [Fact]
    public async Task SetManualReviewAsync_PersistsHumanEditedFlag()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = """
                CREATE TABLE Applications (
                    Id TEXT NOT NULL PRIMARY KEY,
                    JobId TEXT NOT NULL,
                    CandidateRef TEXT NOT NULL DEFAULT '',
                    CandidateName TEXT NULL,
                    CandidateEmail TEXT NULL,
                    Status TEXT NOT NULL,
                    FinalScore REAL NULL,
                    FinalDecision TEXT NULL,
                    Variance REAL NULL,
                    Flagged INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    TestRunId TEXT NULL,
                    LastError TEXT NULL
                );

                CREATE TABLE ManualReviews (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ApplicationId TEXT NOT NULL,
                    RubricScoresJson TEXT NOT NULL,
                    OverallComment TEXT NOT NULL,
                    AdjustedFinalScore REAL NULL,
                    AuditTrailJson TEXT NOT NULL,
                    HumanEdited INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            await setupCommand.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new AppDbContext(options);
        var repository = new ApplicationRepository(context);

        var application = new TalentMatch.Domain.Entities.Application
        {
            JobId = "job-1",
            Status = "Queued"
        };
        await repository.AddAsync(application);

        var review = new ManualReviewData
        {
            ApplicationId = application.Id,
            RubricScoresJson = "{}",
            OverallComment = "Recruiter override",
            AuditTrailJson = "[]",
            HumanEdited = true,
        };

        await repository.SetManualReviewAsync(review);
        var stored = await repository.GetManualReviewAsync(application.Id);

        stored.Should().NotBeNull();
        stored!.HumanEdited.Should().BeTrue();
    }
}
