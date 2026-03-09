using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record UpdateJobConfigCommand(
    string JobId,
    string? RubricJson,
    string? MustHaveCriteriaJson,
    string? DesiredCriteriaJson,
    int ScoringRunCount,
    string AggregationStrategy,
    double LonglistThreshold,
    double ShortlistThreshold,
    double VarianceThreshold
) : IRequest<JobConfigVersion>;

public class UpdateJobConfigCommandHandler : IRequestHandler<UpdateJobConfigCommand, JobConfigVersion>
{
    private readonly IJobRepository _jobRepository;

    public UpdateJobConfigCommandHandler(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public async Task<JobConfigVersion> Handle(UpdateJobConfigCommand request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");

        var versions = await _jobRepository.GetConfigVersionsAsync(request.JobId, cancellationToken);
        var nextVersion = versions.Count + 1;

        var configVersion = new JobConfigVersion
        {
            JobId = request.JobId,
            VersionNumber = nextVersion,
            RubricJson = request.RubricJson ?? "[]",
            MustHaveCriteriaJson = request.MustHaveCriteriaJson ?? "[]",
            DesiredCriteriaJson = request.DesiredCriteriaJson ?? "[]",
            ScoringRunCount = request.ScoringRunCount,
            AggregationStrategy = request.AggregationStrategy,
            LonglistThreshold = request.LonglistThreshold,
            ShortlistThreshold = request.ShortlistThreshold,
            VarianceThreshold = request.VarianceThreshold
        };

        job.CurrentConfigVersionId = configVersion.Id;
        job.UpdatedAt = DateTime.UtcNow;

        await _jobRepository.AddConfigVersionAsync(configVersion, cancellationToken);
        await _jobRepository.UpdateAsync(job, cancellationToken);

        return configVersion;
    }
}
