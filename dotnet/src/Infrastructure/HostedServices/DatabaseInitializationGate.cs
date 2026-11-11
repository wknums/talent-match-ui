namespace TalentMatch.Infrastructure.HostedServices;

// Lets the host hold background workers back while the database schema is still being created in the background
// (Azure SQL start-up). The gate is open by default, so hosts that initialize the database before starting never wait.
public sealed class DatabaseInitializationGate
{
    private volatile TaskCompletionSource _opened = Completed();

    // Call before the host starts workers. Safe to call once; Open releases everything waiting.
    public void Close() => _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _opened.TrySetResult();

    public Task WaitAsync(CancellationToken cancellationToken) => _opened.Task.WaitAsync(cancellationToken);

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
