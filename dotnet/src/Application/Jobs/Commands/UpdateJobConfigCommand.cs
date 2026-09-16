using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
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
    double VarianceThreshold,
    string? RubricSource,
    string? RawExtractionResponse,
    string? ExtractionId,
    string? ExtractionInstructionVersionId,
    string? ExpectedConfigVersionId = null,
    string? RubricApprovalStatus = null
) : IRequest<JobConfigVersion>;

public class UpdateJobConfigCommandHandler : IRequestHandler<UpdateJobConfigCommand, JobConfigVersion>
{
    private readonly IJobRepository _jobRepository;
    private readonly IJobSpecExtractionRepository? _jobSpecExtractionRepository;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;

    public UpdateJobConfigCommandHandler(
        IJobRepository jobRepository,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizationRepository = null,
        IJobSpecExtractionRepository? jobSpecExtractionRepository = null)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
        _jobSpecExtractionRepository = jobSpecExtractionRepository;
    }

    public async Task<JobConfigVersion> Handle(UpdateJobConfigCommand request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");

        await JobAuthorization.EnsureCanMutateAsync(
            job, _currentUser, _organizationRepository, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.ExpectedConfigVersionId)
            && !string.Equals(job.CurrentConfigVersionId, request.ExpectedConfigVersionId, StringComparison.Ordinal))
        {
            throw new JobConfigVersionConflictException();
        }

        string? extractionIdToLink = null;
        var extractionRepository = _jobSpecExtractionRepository;
        if (!string.IsNullOrWhiteSpace(request.ExtractionId) && extractionRepository is not null)
        {
            var extraction = await extractionRepository.GetByIdAsync(request.ExtractionId, cancellationToken)
                ?? throw new InvalidJobConfigException(
                    $"Job specification extraction '{request.ExtractionId}' was not found.");

            if (!string.IsNullOrWhiteSpace(extraction.JobId)
                && !string.Equals(extraction.JobId, request.JobId, StringComparison.Ordinal))
            {
                throw new InvalidJobConfigException(
                    "The extraction record belongs to a different job.");
            }

            // An extraction remains linked to the config version it originally created.
            // Later edited versions retain its provenance without moving that historical link.
            if (string.IsNullOrWhiteSpace(extraction.JobConfigVersionId))
                extractionIdToLink = request.ExtractionId;
        }

        var versions = await _jobRepository.GetConfigVersionsAsync(request.JobId, cancellationToken);
        var nextVersion = versions.Count + 1;

        var rubricApprovalStatus = NormalizeRubricApprovalStatus(request.RubricApprovalStatus);

        var configVersion = new JobConfigVersion
        {
            JobId = request.JobId,
            VersionNumber = nextVersion,
            RubricJson = request.RubricJson ?? "[]",
            MustHaveCriteriaJson = request.MustHaveCriteriaJson ?? "[]",
            MustHavesJson = request.MustHaveCriteriaJson ?? "[]",
            DesiredCriteriaJson = request.DesiredCriteriaJson ?? "[]",
            ScoringRunCount = request.ScoringRunCount,
            RunsPerApplication = request.ScoringRunCount,
            AggregationStrategy = request.AggregationStrategy,
            LonglistThreshold = request.LonglistThreshold,
            ShortlistThreshold = request.ShortlistThreshold,
            VarianceThreshold = request.VarianceThreshold,
            RubricApprovalStatus = rubricApprovalStatus,
            RubricSource = request.RubricSource ?? "manual",
            RawExtractionResponse = request.RawExtractionResponse,
            ExtractionId = request.ExtractionId,
            ExtractionInstructionVersionId = request.ExtractionInstructionVersionId,
        };

        job.CurrentConfigVersionId = configVersion.Id;
        job.UpdatedAt = DateTime.UtcNow;

        await _jobRepository.AddConfigVersionAsync(configVersion, cancellationToken);
        await _jobRepository.UpdateAsync(job, cancellationToken);
        if (extractionIdToLink is not null && extractionRepository is not null)
            await extractionRepository.LinkToJobConfigAsync(extractionIdToLink, request.JobId, configVersion.Id, cancellationToken);

        return configVersion;
    }

    private static string NormalizeRubricApprovalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "draft";

        var normalized = status.Trim().ToLowerInvariant();
        if (normalized is "approved" or "draft")
            return normalized;

        throw new InvalidJobConfigException(
            $"Invalid rubric approval status: '{status}'. Must be 'approved' or 'draft'.");
    }
}

public sealed class JobConfigVersionConflictException()
    : InvalidOperationException("stale_version: the current configuration has changed and must be reloaded before saving.");

public sealed class InvalidJobConfigException(string message) : InvalidOperationException(message);
