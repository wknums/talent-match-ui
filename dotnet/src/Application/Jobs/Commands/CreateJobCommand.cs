using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record CreateJobCommand(
    string Title,
    string Department,
    string Organisation,
    DateTime PostingDate,
    string? RubricJson,
    string? MustHaveCriteriaJson,
    string? DesiredCriteriaJson,
    int ScoringRunCount,
    string AggregationStrategy,
    double LonglistThreshold,
    double ShortlistThreshold,
    double VarianceThreshold,
    string? JobDescription,
    string? RubricSource,
    string? RawExtractionResponse
) : IRequest<Job>;

public class CreateJobCommandHandler : IRequestHandler<CreateJobCommand, Job>
{
    private readonly IJobRepository _jobRepository;

    public CreateJobCommandHandler(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public async Task<Job> Handle(CreateJobCommand request, CancellationToken cancellationToken)
    {
        var job = new Job
        {
            JobCode = $"JOB-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}",
            Title = request.Title,
            Department = request.Department,
            Organisation = request.Organisation,
            PostingDate = request.PostingDate,
            Status = "active",
            JobDescription = request.JobDescription
        };

        var configVersion = new JobConfigVersion
        {
            JobId = job.Id,
            VersionNumber = 1,
            RubricJson = request.RubricJson ?? "[]",
            MustHaveCriteriaJson = request.MustHaveCriteriaJson ?? "[]",
            DesiredCriteriaJson = request.DesiredCriteriaJson ?? "[]",
            ScoringRunCount = request.ScoringRunCount > 0 ? request.ScoringRunCount : 3,
            AggregationStrategy = request.AggregationStrategy ?? "median",
            LonglistThreshold = request.LonglistThreshold,
            ShortlistThreshold = request.ShortlistThreshold,
            VarianceThreshold = request.VarianceThreshold > 0 ? request.VarianceThreshold : 15,
            RubricSource = request.RubricSource ?? "manual",
            RawExtractionResponse = request.RawExtractionResponse
        };

        job.CurrentConfigVersionId = configVersion.Id;

        await _jobRepository.AddAsync(job, cancellationToken);
        await _jobRepository.AddConfigVersionAsync(configVersion, cancellationToken);

        return job;
    }
}
