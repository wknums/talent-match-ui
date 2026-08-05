using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
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
    string? RawExtractionResponse,
    string? OrganizationId = null,
    string? DepartmentId = null
) : IRequest<Job>;

public class CreateJobCommandHandler : IRequestHandler<CreateJobCommand, Job>
{
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;

    public CreateJobCommandHandler(
        IJobRepository jobRepository,
        ICurrentUserService currentUser,
        IOrganizationRepository? organizationRepository = null)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
    }

    public async Task<Job> Handle(CreateJobCommand request, CancellationToken cancellationToken)
    {
        var job = new Job
        {
            JobCode = $"JOB-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}",
            Title = request.Title,
            Department = request.Department,
            Organisation = request.Organisation,
            OrganizationId = request.OrganizationId,
            DepartmentId = request.DepartmentId,
            PostingDate = request.PostingDate,
            Status = "active",
            JobDescription = request.JobDescription,
            CreatedBy = _currentUser.UserId ?? _currentUser.Username
        };

        var authorizationState = await _currentUser.GetAuthorizationStateAsync(cancellationToken);
        if (authorizationState is not null)
        {
            await JobAuthorization.EnsureValidScopeAsync(
                job, authorizationState, _organizationRepository, cancellationToken);
            if (!JobAuthorization.CanMutate(authorizationState, job))
                throw new UnauthorizedAccessException("The current user cannot create jobs in this scope.");
        }

        var configVersion = new JobConfigVersion
        {
            JobId = job.Id,
            VersionNumber = 1,
            RubricJson = request.RubricJson ?? "[]",
            MustHaveCriteriaJson = request.MustHaveCriteriaJson ?? "[]",
            MustHavesJson = request.MustHaveCriteriaJson ?? "[]",
            DesiredCriteriaJson = request.DesiredCriteriaJson ?? "[]",
            ScoringRunCount = request.ScoringRunCount > 0 ? request.ScoringRunCount : 3,
            RunsPerApplication = request.ScoringRunCount > 0 ? request.ScoringRunCount : 3,
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
