using FluentAssertions;
using Moq;
using TalentMatch.Application.JobExtraction.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class JobSpecExtractionDiagnosticsTests
{
    [Fact]
    public async Task Query_ReturnsLatestExtractionForJob()
    {
        var repo = new Mock<IJobSpecExtractionRepository>();
        repo.Setup(repository => repository.GetLatestForJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobSpecExtraction
            {
                Id = "extract-2",
                JobId = "job-1",
                InstructionVersionId = "instruction-v2",
                ProtectedContractVersion = "extraction-rubric-v1",
                ValidationStatus = "valid",
                ValidationFindingsJson = """[{"code":"duplicate_requirement","severity":"warning","path":"$.requirements[1]","message":"duplicate"}]""",
                SourceFileName = "sample-spec.md",
                SourceMimeType = "text/markdown",
                SourceSha256 = "hash",
                RawResponse = "{}",
                NormalizedResponseJson = "{}",
                CreatedBy = "admin",
                CompletedAt = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc),
                CorrelationId = Guid.NewGuid().ToString()
            });

        var handler = new GetJobSpecExtractionQueryHandler(repo.Object);

        var result = await handler.Handle(new GetJobSpecExtractionQuery("job-1"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.InstructionVersionId.Should().Be("instruction-v2");
        result.ValidationFindings.Should().ContainSingle(finding => finding.Code == "duplicate_requirement");
    }
}
