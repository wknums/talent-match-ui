using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Server.Services;

public enum AzureSqlReadinessStatus
{
    Ready,
    Resuming,
    Unavailable,
}

public sealed record AzureSqlReadinessResult(
    AzureSqlReadinessStatus Status,
    string? ErrorCode = null,
    string? Detail = null)
{
    public static AzureSqlReadinessResult Ready() => new(AzureSqlReadinessStatus.Ready);
}

public sealed record AzureSqlProbeResult(
    AzureSqlReadinessStatus Status,
    string? Detail = null)
{
    public static AzureSqlProbeResult Ready() => new(AzureSqlReadinessStatus.Ready);

    public static AzureSqlProbeResult TransientFailure(string detail) =>
        new(AzureSqlReadinessStatus.Resuming, detail);

    public static AzureSqlProbeResult PermanentFailure(string detail) =>
        new(AzureSqlReadinessStatus.Unavailable, detail);
}

public interface IAzureSqlProbe
{
    Task<AzureSqlProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

public interface IAzureSqlReadinessService
{
    Task<AzureSqlReadinessResult> WaitUntilReadyAsync(CancellationToken cancellationToken);
}

public sealed class AzureSqlReadinessOptions
{
    public int MaxAttempts { get; init; } = 5;

    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    public TimeSpan ReadyCacheDuration { get; init; } = TimeSpan.FromSeconds(30);
}

public static class AzureSqlErrorClassifier
{
    private static readonly HashSet<int> TransientErrorNumbers =
    [
        40501,
        40613,
        40197,
        10928,
        10929,
        49918,
        49919,
        49920,
        -2,
    ];

    public static bool IsTransientErrorNumber(int errorNumber) =>
        TransientErrorNumbers.Contains(errorNumber);

    public static bool IsTransient(Exception exception)
    {
        if (exception is TimeoutException)
            return true;

        if (exception is SqlException sqlException
            && sqlException.Errors.Cast<SqlError>().Any(error => IsTransientErrorNumber(error.Number)))
        {
            return true;
        }

        return exception.InnerException is not null && IsTransient(exception.InnerException);
    }
}

public sealed class AzureSqlReadinessService : IAzureSqlReadinessService
{
    private readonly object _sync = new();
    private readonly IAzureSqlProbe _probe;
    private readonly AzureSqlReadinessOptions _options;
    private Task<AzureSqlReadinessResult>? _inFlight;
    private DateTimeOffset _readyUntil;

    public AzureSqlReadinessService(IAzureSqlProbe probe, AzureSqlReadinessOptions options)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);

        _probe = probe;
        _options = options;
    }

    public async Task<AzureSqlReadinessResult> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        Task<AzureSqlReadinessResult> readinessTask;
        lock (_sync)
        {
            if (_readyUntil > DateTimeOffset.UtcNow)
                return AzureSqlReadinessResult.Ready();

            readinessTask = _inFlight ??= ProbeWithRetryAsync();
        }

        try
        {
            return await readinessTask.WaitAsync(cancellationToken);
        }
        finally
        {
            if (readinessTask.IsCompleted)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_inFlight, readinessTask))
                        _inFlight = null;
                }
            }
        }
    }

    private async Task<AzureSqlReadinessResult> ProbeWithRetryAsync()
    {
        AzureSqlProbeResult? lastResult = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            lastResult = await _probe.ProbeAsync(CancellationToken.None);
            if (lastResult.Status == AzureSqlReadinessStatus.Ready)
            {
                lock (_sync)
                    _readyUntil = DateTimeOffset.UtcNow.Add(_options.ReadyCacheDuration);

                return AzureSqlReadinessResult.Ready();
            }

            if (lastResult.Status == AzureSqlReadinessStatus.Unavailable)
            {
                return new AzureSqlReadinessResult(
                    AzureSqlReadinessStatus.Unavailable,
                    "database_unavailable",
                    lastResult.Detail);
            }

            if (attempt < _options.MaxAttempts)
            {
                var delayIndex = Math.Min(attempt - 1, _options.RetryDelays.Count - 1);
                var delay = delayIndex >= 0 ? _options.RetryDelays[delayIndex] : TimeSpan.Zero;
                await Task.Delay(delay);
            }
        }

        return new AzureSqlReadinessResult(
            AzureSqlReadinessStatus.Resuming,
            "database_resuming",
            lastResult?.Detail);
    }
}

public sealed class EfCoreAzureSqlProbe(
    IServiceScopeFactory scopeFactory,
    ILogger<EfCoreAzureSqlProbe> logger) : IAzureSqlProbe
{
    public async Task<AzureSqlProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return AzureSqlProbeResult.Ready();
        }
        catch (Exception exception) when (AzureSqlErrorClassifier.IsTransient(exception))
        {
            logger.LogInformation(exception, "Azure SQL is resuming; readiness probe will be retried.");
            return AzureSqlProbeResult.TransientFailure(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Azure SQL readiness probe failed with a permanent error.");
            return AzureSqlProbeResult.PermanentFailure(exception.Message);
        }
    }
}