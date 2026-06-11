using System.Collections.Concurrent;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Scoring.Commands;
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
    List<string> Errors
);

public class ProcessJobCommandHandler : IRequestHandler<ProcessJobCommand, ProcessJobResult>
{
    private readonly IJobRepository _jobRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IScoringBatchRepository _batchRepo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessJobCommandHandler> _logger;

    private static readonly string? SeqEndpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
    private static readonly string? PlatformEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");
    private static readonly int MaxParallel = int.TryParse(Environment.GetEnvironmentVariable("AWR_MAX_PARALLEL"), out var p) && p > 0 ? p : 1;
    private static readonly int PlatformBatchSize = int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_BATCH_SIZE"), out var b) && b > 0 ? b : 2;

    public static string ResolveScoringMode()
    {
        if (string.IsNullOrEmpty(PlatformEndpoint) || PlatformEndpoint == SeqEndpoint)
            return "sequential";
        return "platform";
    }

    static ProcessJobCommandHandler()
    {
        var mode = ResolveScoringMode();
        Console.WriteLine($"[Pipeline] Scoring mode resolved: {mode} (SEQ={SeqEndpoint ?? "(unset)"}, PLATFORM={PlatformEndpoint ?? "(unset)"})");
    }

    public ProcessJobCommandHandler(
        IJobRepository jobRepo,
        IApplicationRepository applicationRepo,
        IScoringBatchRepository batchRepo,
        IServiceScopeFactory scopeFactory,
        ILogger<ProcessJobCommandHandler> logger)
    {
        _jobRepo = jobRepo;
        _applicationRepo = applicationRepo;
        _batchRepo = batchRepo;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ProcessJobResult> Handle(ProcessJobCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found");

        var jobDescriptionText = job.JobDescription ?? job.Title;
        var config = job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
            ?? job.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        var varianceThreshold = config?.VarianceThreshold ?? 15;
        var longlistThreshold = config?.LonglistThreshold ?? 70;

        // Load as no-tracking just to get the list of IDs to process
        var applications = await _applicationRepo.GetByJobIdAsync(request.JobId, ct);
        var toProcessIds = applications
            .Where(a => a.Status is "Queued" or "Scored" or "Scoring" or "ScoringFailed")
            .Select(a => a.Id).ToList();

        // Platform mode: enqueue ScoringBatches and return immediately. The
        // in-process reconciler (PlatformScoringReconciler hosted service) drives
        // submit/poll/finalize asynchronously. Sequential mode below is unchanged.
        if (ResolveScoringMode() == "platform")
        {
            var existingBatches = await _batchRepo.ListByJobAsync(request.JobId, ct);
            var activeBatchCount = existingBatches.Count(b => b.Status is "pending" or "submitted");
            if (activeBatchCount > 0)
            {
                var message = $"Job {request.JobId} already has {activeBatchCount} in-flight platform batch(es).";
                _logger.LogWarning(message);
                return new ProcessJobResult(0, toProcessIds.Count, new List<string> { message });
            }

            using var pScope = _scopeFactory.CreateScope();
            var batchRepo = pScope.ServiceProvider.GetRequiredService<IScoringBatchRepository>();
            var appRepoP = pScope.ServiceProvider.GetRequiredService<IApplicationRepository>();
            var batches = 0;
            foreach (var chunk in toProcessIds.Chunk(PlatformBatchSize))
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
                    try
                    {
                        var a = await appRepoP.GetByIdAsync(aId, ct);
                        if (a != null) { a.Status = "Scoring"; await appRepoP.UpdateAsync(a, ct); }
                    }
                    catch { /* best effort */ }
                }
            }
            await batchRepo.InitProgressAsync(request.JobId, toProcessIds.Count, batches, ct);
            _logger.LogInformation("Job {JobId}: enqueued {Batches} platform batch(es) for {Apps} application(s).",
                request.JobId, batches, toProcessIds.Count);
            return new ProcessJobResult(toProcessIds.Count, toProcessIds.Count, new List<string>());
        }

        int processed = 0;
        var errors = new ConcurrentBag<string>();
        _logger.LogInformation("Processing job {JobId}: {Count} applications in {Mode} mode (parallelism: {MaxParallel})",
            request.JobId, toProcessIds.Count, ResolveScoringMode(), MaxParallel);

        // Process apps in parallel — each in its own DI scope to avoid EF Core tracking conflicts.
        // AWR_MAX_PARALLEL controls concurrency (default 1 = sequential).
        await Parallel.ForEachAsync(toProcessIds,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = ct },
            async (appId, token) =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var appRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();

                // Mark as Scoring
                var app = await appRepo.GetByIdAsync(appId, ct)
                    ?? throw new InvalidOperationException($"Application {appId} not found");
                app.Status = "Scoring";
                await appRepo.UpdateAsync(app, ct);

                // Score via ScoreApplicationCommand (same as test scoring)
                var scoringResult = await mediator.Send(
                    new ScoreApplicationCommand(appId, request.JobId, request.RunCount,
                        request.ProductionPromptId, jobDescriptionText, config?.RubricJson), ct);

                // Aggregation + decision + AggregatedResult persistence shared with platform mode.
                var finalizer = scope.ServiceProvider.GetRequiredService<IApplicationScoringFinalizer>();
                await finalizer.FinalizeAsync(appId, request.JobId, scoringResult.Runs,
                    request.RunCount, varianceThreshold, longlistThreshold, ct);

                Interlocked.Increment(ref processed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process application {AppId} in job {JobId}",
                    appId, request.JobId);
                errors.Add($"Application {appId}: {ex.Message}");

                try
                {
                    using var errScope = _scopeFactory.CreateScope();
                    var errRepo = errScope.ServiceProvider.GetRequiredService<IApplicationRepository>();
                    var failedApp = await errRepo.GetByIdAsync(appId, ct);
                    if (failedApp != null)
                    {
                        failedApp.Status = "ScoringFailed";
                        failedApp.LastError = ex.Message;
                        await errRepo.UpdateAsync(failedApp, ct);
                    }

                    var dlqRepo = errScope.ServiceProvider.GetRequiredService<IFailureQueueRepository>();
                    await dlqRepo.AddAsync(new FailureQueueItem
                    {
                        EntityType = "Application",
                        EntityId = appId,
                        FailureReason = ex.Message,
                    }, ct);
                }
                catch { /* best effort */ }
            }
        });

        return new ProcessJobResult(processed, toProcessIds.Count, errors.ToList());
    }
}