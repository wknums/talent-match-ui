namespace TalentMatch.Web.Client.Services;

public sealed class AsyncRefreshLoop(
    TimeSpan interval,
    TimeProvider clock,
    Func<CancellationToken, Task> refresh,
    Func<Exception, Task> reportError) : IAsyncDisposable
{
    private TimeSpan currentInterval = ValidateInterval(interval);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private CancellationTokenSource? waiting;
    private Task? task;
    private bool disposed;

    public CancellationToken Token => lifetime.Token;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        task ??= RunAsync();
    }

    public void UpdateInterval(TimeSpan value)
    {
        ValidateInterval(value);
        lock (sync)
        {
            if (disposed || currentInterval == value)
                return;
            currentInterval = value;
            waiting?.Cancel();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                CancellationTokenSource delay;
                TimeSpan duration;
                lock (sync)
                {
                    duration = currentInterval;
                    waiting = delay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                }
                try
                {
                    await Task.Delay(duration, clock, delay.Token);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    continue;
                }
                finally
                {
                    lock (sync)
                    {
                        waiting = null;
                        delay.Dispose();
                    }
                }
                await refresh(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await reportError(exception);
        }
    }

    private static TimeSpan ValidateInterval(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
        }
        await lifetime.CancelAsync();
        if (task is not null)
            await task;
        lifetime.Dispose();
    }
}
