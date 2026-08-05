using Microsoft.JSInterop;

namespace TalentMatch.Web.Client.Services;

public enum NavigationAuditAction
{
    Collapse,
    Expand,
}

public sealed record NavigationShellBrowserSnapshot(bool DesktopCollapsed, bool IsCompact);

public sealed record NavigationAuditRequest(
    NavigationAuditAction Action,
    Guid CorrelationId,
    DateTimeOffset RequestedAt);

public sealed record NavigationAuditResult(Guid CorrelationId);

public sealed record NavigationShellNotification(string Message, bool IsRetryable);

public interface INavigationShellBrowser : IAsyncDisposable
{
    ValueTask<NavigationShellBrowserSnapshot> InitializeAsync(
        Func<bool, ValueTask> breakpointChanged,
        Func<ValueTask> escapePressed,
        CancellationToken cancellationToken = default);

    ValueTask PersistDesktopCollapsedAsync(bool collapsed, CancellationToken cancellationToken = default);
}

public interface INavigationAuditClient
{
    Task<NavigationAuditResult> RecordNavigationAsync(
        NavigationAuditRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Desktop collapse is a durable, audited preference; the compact overlay is transient and never persisted.
/// </summary>
public sealed class NavigationShellState(
    INavigationShellBrowser browser,
    INavigationAuditClient auditClient) : IAsyncDisposable, IDisposable
{
    private const string AuditFailureMessage =
        "Navigation could not be updated because the change could not be recorded. Try again.";

    private bool _initialized;
    private bool _disposed;

    public bool DesktopCollapsed { get; private set; }
    public bool IsCompact { get; private set; }
    public bool CompactOverlayOpen { get; private set; }
    public NavigationShellNotification? Notification { get; private set; }

    public string RegionElementId => "navigation-shell-sidebar";
    public bool NavigationVisible => IsCompact ? CompactOverlayOpen : !DesktopCollapsed;
    public string ToggleLabel => NavigationVisible ? "Collapse navigation" : "Expand navigation";

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || _disposed)
            return;

        _initialized = true;
        var snapshot = await browser.InitializeAsync(OnBreakpointChangedAsync, OnEscapePressedAsync, cancellationToken);
        DesktopCollapsed = snapshot.DesktopCollapsed;
        IsCompact = snapshot.IsCompact;
        CompactOverlayOpen = false;
        Notification = null;
        Notify();
    }

    public async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        var requestedVisible = !NavigationVisible;
        var action = requestedVisible ? NavigationAuditAction.Expand : NavigationAuditAction.Collapse;
        var request = new NavigationAuditRequest(action, Guid.NewGuid(), DateTimeOffset.UtcNow);

        try
        {
            await auditClient.RecordNavigationAsync(request, cancellationToken);
        }
        catch
        {
            // The prior state stays visible: an unrecorded change must never appear to have happened.
            Notification = new NavigationShellNotification(AuditFailureMessage, IsRetryable: true);
            Notify();
            return;
        }

        if (_disposed)
            return;

        if (IsCompact)
        {
            CompactOverlayOpen = requestedVisible;
        }
        else
        {
            DesktopCollapsed = !requestedVisible;
            await browser.PersistDesktopCollapsedAsync(DesktopCollapsed, cancellationToken);
        }

        Notification = null;
        Notify();
    }

    public void OnRouteChanged()
    {
        if (!CompactOverlayOpen)
            return;

        CompactOverlayOpen = false;
        Notify();
    }

    public void CloseCompactOverlay() => OnRouteChanged();

    private ValueTask OnBreakpointChangedAsync(bool isCompact)
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        IsCompact = isCompact;
        CompactOverlayOpen = false;
        Notify();
        return ValueTask.CompletedTask;
    }

    private ValueTask OnEscapePressedAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        OnRouteChanged();
        return ValueTask.CompletedTask;
    }

    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        Changed = null;
        await browser.DisposeAsync();
    }

    /// <summary>
    /// Supports containers that only dispose synchronously; browser teardown is best effort
    /// because it cannot be awaited here.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Changed = null;
        _ = browser.DisposeAsync();
    }
}

/// <summary>Adapter over the maintained <c>navigationShell.js</c> module.</summary>
public sealed class NavigationShellBrowser(IJSRuntime jsRuntime) : INavigationShellBrowser
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<NavigationShellInterop>? _callbacks;
    private bool _disposed;

    public async ValueTask<NavigationShellBrowserSnapshot> InitializeAsync(
        Func<bool, ValueTask> breakpointChanged,
        Func<ValueTask> escapePressed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _module = await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, "./js/navigationShell.js");
            _callbacks = DotNetObjectReference.Create(
                new NavigationShellInterop(breakpointChanged, escapePressed));
            return await _module.InvokeAsync<NavigationShellBrowserSnapshot>(
                "initialize", cancellationToken, _callbacks);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
        {
            // Prerendering and unsupported hosts fall back to the expanded desktop default.
            return new NavigationShellBrowserSnapshot(DesktopCollapsed: false, IsCompact: false);
        }
    }

    public async ValueTask PersistDesktopCollapsedAsync(
        bool collapsed,
        CancellationToken cancellationToken = default)
    {
        if (_module is null)
            return;

        try
        {
            await _module.InvokeVoidAsync("persistDesktopCollapsed", cancellationToken, collapsed);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
            catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
            {
            }

            _module = null;
        }

        _callbacks?.Dispose();
        _callbacks = null;
    }
}

public sealed class NavigationShellInterop(
    Func<bool, ValueTask> breakpointChanged,
    Func<ValueTask> escapePressed)
{
    [JSInvokable]
    public ValueTask OnBreakpointChanged(bool isCompact) => breakpointChanged(isCompact);

    [JSInvokable]
    public ValueTask OnEscapePressed() => escapePressed();
}
