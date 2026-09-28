using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public sealed class JobSpecExtractionRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _context;

    public JobSpecExtractionRepositoryTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Repository_PersistsExtractionHistory_AndLoadsLatestForJob()
    {
        var job = new Job
        {
            Id = "job-001",
            Title = "Senior Data Platform Engineer",
            Department = "Data Engineering",
            Organisation = "Northwind Analytics",
            PostingDate = DateTime.UtcNow,
            Status = "active",
        };

        var instruction = new ExtractionInstructionVersion
        {
            Id = "instruction-v1",
            VersionNumber = 1,
            InstructionText = "Extract every independently assessable requirement as its own item.",
            ProtectedContractVersion = "extraction-rubric-v1",
            Status = "active",
            ValidationStatus = "valid",
            CreatedBy = "seed",
        };

        _context.Jobs.Add(job);
        _context.ExtractionInstructionVersions.Add(instruction);
        await _context.SaveChangesAsync();

        var repository = new JobSpecExtractionRepository(_context);
        var first = new JobSpecExtraction
        {
            Id = "extract-001",
            Purpose = "job_creation",
            InstructionVersionId = instruction.Id,
            ProtectedContractVersion = instruction.ProtectedContractVersion,
            SourceFileName = "sample-spec.md",
            SourceMimeType = "text/markdown",
            SourceSha256 = "abc123",
            RawResponse = "{}",
            NormalizedResponseJson = "{}",
            ValidationStatus = "valid",
            ValidationFindingsJson = "[]",
            JobId = job.Id,
            JobConfigVersionId = "cfg-001",
            CreatedBy = "recruiter-1",
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            CorrelationId = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        var second = new JobSpecExtraction
        {
            Id = "extract-002",
            Purpose = "job_creation",
            InstructionVersionId = instruction.Id,
            ProtectedContractVersion = instruction.ProtectedContractVersion,
            SourceFileName = "sample-spec.md",
            SourceMimeType = "text/markdown",
            SourceSha256 = "def456",
            RawResponse = "{}",
            NormalizedResponseJson = "{}",
            ValidationStatus = "valid",
            ValidationFindingsJson = "[]",
            JobId = job.Id,
            JobConfigVersionId = "cfg-002",
            CreatedBy = "recruiter-1",
            CompletedAt = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
        };

        await repository.AddAsync(first);
        await repository.AddAsync(second);

        var latest = await repository.GetLatestForJobAsync(job.Id);
        var byConfig = await repository.GetByConfigVersionIdAsync("cfg-001");

        latest.Should().NotBeNull();
        latest!.Id.Should().Be(second.Id);
        byConfig.Should().NotBeNull();
        byConfig!.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task JobRepository_Delete_DetachesExtractionHistoryBeforeDeletingJob()
    {
        var job = new Job
        {
            Id = "job-delete",
            Title = "Delete Me",
            Department = "Engineering",
            Organisation = "Northwind",
            PostingDate = DateTime.UtcNow,
            Status = "active",
        };
        var instruction = new ExtractionInstructionVersion
        {
            Id = "instruction-delete",
            VersionNumber = 2,
            InstructionText = "Extract requirements.",
            ProtectedContractVersion = "extraction-rubric-v1",
            Status = "retired",
            ValidationStatus = "valid",
            CreatedBy = "seed",
        };
        var extraction = new JobSpecExtraction
        {
            Id = "extract-delete",
            Purpose = "job_creation",
            InstructionVersionId = instruction.Id,
            ProtectedContractVersion = instruction.ProtectedContractVersion,
            SourceFileName = "spec.md",
            SourceMimeType = "text/markdown",
            SourceSha256 = "abc123",
            RawResponse = "{}",
            ValidationStatus = "valid",
            ValidationFindingsJson = "[]",
            JobId = job.Id,
            JobConfigVersionId = "cfg-delete",
            CreatedBy = "admin",
            CorrelationId = Guid.NewGuid().ToString(),
        };
        _context.AddRange(job, instruction, extraction);
        await _context.SaveChangesAsync();

        await new JobRepository(_context).DeleteAsync(job.Id);

        (await _context.Jobs.FindAsync(job.Id)).Should().BeNull();
        var preservedExtraction = await _context.JobSpecExtractions.AsNoTracking().SingleAsync(item => item.Id == extraction.Id);
        preservedExtraction.JobId.Should().BeNull();
        preservedExtraction.JobConfigVersionId.Should().BeNull();
    }

    [Fact]
    public async Task ExtractionInstructionRepository_Activate_RetiresPreviousActiveVersion()
    {
        var active = new ExtractionInstructionVersion
        {
            Id = "instruction-active",
            VersionNumber = 3,
            InstructionText = "Old active instructions",
            ProtectedContractVersion = "extraction-rubric-v1",
            Status = "active",
            ValidationStatus = "valid",
            CreatedBy = "seed",
        };
        var draft = new ExtractionInstructionVersion
        {
            Id = "instruction-draft",
            VersionNumber = 4,
            InstructionText = "New instructions",
            ProtectedContractVersion = "extraction-rubric-v1",
            Status = "draft",
            ValidationStatus = "valid",
            CreatedBy = "admin",
        };
        _context.AddRange(active, draft);
        await _context.SaveChangesAsync();

        var activatedAt = DateTime.UtcNow;
        await new ExtractionInstructionRepository(_context).ActivateAsync(draft.Id, "admin", activatedAt);

        var versions = await _context.ExtractionInstructionVersions.AsNoTracking().ToDictionaryAsync(item => item.Id);
        versions[active.Id].Status.Should().Be("retired");
        versions[draft.Id].Status.Should().Be("active");
        versions[draft.Id].ActivatedBy.Should().Be("admin");
        versions[draft.Id].ActivatedAt.Should().BeCloseTo(activatedAt, TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
