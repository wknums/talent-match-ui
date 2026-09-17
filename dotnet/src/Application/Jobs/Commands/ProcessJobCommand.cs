using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record ProcessJobCommand(
    string JobId,
    string ProductionPromptId,
    int RunCount
) : IRequest<ProcessJobResult>;

public record ProcessJobResult(
    int Processed,
    int Total,
    List<string> Errors,
    int Queued = 0
);

public class ProcessJobCommandHandler : IRequestHandler<ProcessJobCommand, ProcessJobResult>
{
    private readonly IJobRepository _jobRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IScoringBatchRepository _batchRepo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessJobCommandHandler> _logger;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;
    private readonly IScoringQueueSignal? _queueSignal;

    public static string ResolveScoringMode()
    {
        var seqEndpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
        var platformEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");
        if (string.IsNullOrEmpty(platformEndpoint) || platformEndpoint == seqEndpoint)
            return "sequential";
        return "platform";
    }

    private static int ResolvePlatformBatchSize()
        => int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_BATCH_SIZE"), out var size) && size > 0
            ? size
            : 2;

    static ProcessJobCommandHandler()
    {
        var mode = ResolveScoringMode();
        Console.WriteLine($"[Pipeline] Scoring mode resolved: {mode}");
    }

    public ProcessJobCommandHandler(
        IJobRepository jobRepo,
        IApplicationRepository applicationRepo,
        IScoringBatchRepository batchRepo,
        IServiceScopeFactory scopeFactory,
        ILogger<ProcessJobCommandHandler> logger,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizationRepository = null,
        IScoringQueueSignal? queueSignal = null)
    {
        _jobRepo = jobRepo;
        _applicationRepo = applicationRepo;
        _batchRepo = batchRepo;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
        _queueSignal = queueSignal;
    }

    public async Task<ProcessJobResult> Handle(ProcessJobCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found");

        await JobAuthorization.EnsureCanMutateAsync(
            job, _currentUser, _organizationRepository, ct);

        var config = job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
            ?? job.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

        var applications = await _applicationRepo.GetByJobIdAsync(request.JobId, ct);
        if (ResolveScoringMode() == "sequential")
        {
            if (!string.Equals(config?.RubricApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Approve the job rubric before processing applications.");

            var queued = applications.Count(application => application.Status == "Queued" && application.TestRunId is null);
            (_queueSignal ?? throw new InvalidOperationException("The sequential scoring pool is not registered.")).Pulse();
            _logger.LogInformation("Job {JobId}: notified the scoring pool of {Queued} queued application(s).",
                request.JobId, queued);
            return new ProcessJobResult(0, queued, new List<string>(), queued);
        }

        var toProcessIds = applications
            .Where(a => a.Status is "Queued" or "Scored" or "Scoring" or "ScoringFailed")
            .Select(a => a.Id).ToList();

        // Platform mode: enqueue ScoringBatches and return immediately. The
        // in-process reconciler (PlatformScoringReconciler hosted service) drives
        // submit/poll/finalize asynchronously.
        if (ResolveScoringMode() == "platform")
        {
            var existingBatches = await _batchRepo.ListByJobAsync(request.JobId, ct);
            var activeBatches = existingBatches
                .Where(batch => batch.Status is "pending" or "submitting" or "submitted" or "cancelling")
                .ToList();
            var activeApplicationIds = activeBatches
                .SelectMany(batch =>
                    JsonSerializer.Deserialize<string[]>(batch.ApplicationIdsJson)
                    ?? throw new InvalidDataException($"Scoring batch {batch.Id} has no application IDs."))
                .ToHashSet(StringComparer.Ordinal);
            var toEnqueueIds = toProcessIds
                .Where(applicationId => !activeApplicationIds.Contains(applicationId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (toEnqueueIds.Count == 0)
            {
                _logger.LogInformation(
                    "Job {JobId}: all {Apps} processable application(s) are already assigned to active platform batches.",
                    request.JobId,
                    toProcessIds.Count);
                return new ProcessJobResult(0, toProcessIds.Count, new List<string>());
            }

            using var pScope = _scopeFactory.CreateScope();
            var batchRepo = pScope.ServiceProvider.GetRequiredService<IScoringBatchRepository>();
            var appRepoP = pScope.ServiceProvider.GetRequiredService<IApplicationRepository>();
            var batches = 0;
            foreach (var chunk in toEnqueueIds.Chunk(ResolvePlatformBatchSize()))
            {
                var batch = new ScoringBatch
                {
                    JobId = request.JobId,
                    PromptVersionId = request.ProductionPromptId,
                    ApplicationIdsJson = JsonSerializer.Serialize(chunk),
                    RunCount = request.RunCount,
                    Status = "pending",
                };
                await batchRepo.CreateAsync(batch, ct);
                batches++;
                foreach (var aId in chunk)
                {
                    var application = await appRepoP.GetByIdAsync(aId, ct)
                        ?? throw new InvalidOperationException($"Application {aId} not found after platform batch creation.");
                    application.Status = "Scoring";
                    await appRepoP.UpdateAsync(application, ct);
                }
            }
            if (activeBatches.Count == 0)
                await batchRepo.InitProgressAsync(request.JobId, toEnqueueIds.Count, batches, ct);
            else
                await batchRepo.AddProgressAsync(request.JobId, toEnqueueIds.Count, batches, ct);
            _logger.LogInformation("Job {JobId}: enqueued {Batches} platform batch(es) for {Apps} application(s).",
                request.JobId, batches, toEnqueueIds.Count);
            return new ProcessJobResult(toEnqueueIds.Count, toProcessIds.Count, new List<string>());
        }

        throw new InvalidOperationException("Unsupported scoring mode.");
    }
}