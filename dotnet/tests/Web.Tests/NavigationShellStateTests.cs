using FluentAssertions;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class NavigationShellStateTests
{
    [Fact]
    public async Task InitializeAsync_RestoresDesktopPreferenceWithoutAuditingOrWriting()
    {
        var browser = new FakeNavigationShellBrowser(
            new NavigationShellBrowserSnapshot(DesktopCollapsed: true, IsCompact: false));
        var audit = new FakeNavigationAuditClient();
        await using var state = new NavigationShellState(browser, audit);

        await state.InitializeAsync();

        state.DesktopCollapsed.Should().BeTrue();
        state.IsCompact.Should().BeFalse();
        state.CompactOverlayOpen.Should().BeFalse();
        state.Notification.Should().BeNull();
        audit.Requests.Should().BeEmpty();
        browser.PersistedDesktopPreferences.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, NavigationAuditAction.Collapse, true)]
    [InlineData(true, NavigationAuditAction.Expand, false)]
    public async Task ToggleAsync_WaitsForMatchingAuditBeforeChangingAndPersistingDesktopPreference(
        bool initialCollapsed,
        NavigationAuditAction expectedAction,
        bool expectedCollapsed)
    {
        var browser = new FakeNavigationShellBrowser(
            new NavigationShellBrowserSnapshot(initialCollapsed, IsCompact: false));
        var audit = new FakeNavigationAuditClient();
        var auditCompletion = new TaskCompletionSource<NavigationAuditResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        audit.OnRecord = _ => auditCompletion.Task;
        await using var state = new NavigationShellState(browser, audit);
        await state.InitializeAsync();

        var toggleTask = state.ToggleAsync();

        var request = audit.Requests.Should().ContainSingle().Subject;
        request.Action.Should().Be(expectedAction);
        request.CorrelationId.Should().NotBeEmpty();
        request.RequestedAt.Should().NotBe(default);
        state.DesktopCollapsed.Should().Be(initialCollapsed,
            "the requested preference cannot be visible before its audit succeeds");
        browser.PersistedDesktopPreferences.Should().BeEmpty(
            "the requested preference cannot be persisted before its audit succeeds");

        auditCompletion.SetResult(new NavigationAuditResult(request.CorrelationId));
        await toggleTask;

        state.DesktopCollapsed.Should().Be(expectedCollapsed);
        state.CompactOverlayOpen.Should().BeFalse();
        browser.PersistedDesktopPreferences.Should().Equal(expectedCollapsed);
        state.Notification.Should().BeNull();
    }

    [Fact]
    public async Task ToggleAsync_AuditFailureRollsBackAndShowsRetryableNotification()
    {
        var browser = new FakeNavigationShellBrowser(
            new NavigationShellBrowserSnapshot(DesktopCollapsed: false, IsCompact: false));
        var audit = new FakeNavigationAuditClient
        {
            OnRecord = _ => Task.FromException<NavigationAuditResult>(
                new InvalidOperationException("Audit service unavailable.")),
        };
        await using var state = new NavigationShellState(browser, audit);
        await state.InitializeAsync();

        await state.ToggleAsync();

        state.DesktopCollapsed.Should().BeFalse();
        state.CompactOverlayOpen.Should().BeFalse();
        browser.PersistedDesktopPreferences.Should().BeEmpty();
        state.Notification.Should().NotBeNull();
        state.Notification!.IsRetryable.Should().BeTrue();
        state.Notification.Message.Should().NotBeNullOrWhiteSpace();

        audit.OnRecord = request => Task.FromResult(new NavigationAuditResult(request.CorrelationId));
        await state.ToggleAsync();

        state.DesktopCollapsed.Should().BeTrue();
        browser.PersistedDesktopPreferences.Should().Equal(true);
        state.Notification.Should().BeNull("a successful retry clears the visible failure state");
    }

    [Fact]
    public async Task ToggleAsync_InCompactMode_ChangesOnlyTransientOverlayState()
    {
        var browser = new FakeNavigationShellBrowser(
            new NavigationShellBrowserSnapshot(DesktopCollapsed: true, IsCompact: true));
        var audit = new FakeNavigationAuditClient();
        await using var state = new NavigationShellState(browser, audit);
        await state.InitializeAsync();

        await state.ToggleAsync();

        state.IsCompact.Should().BeTrue();
        state.CompactOverlayOpen.Should().BeTrue();
        state.DesktopCollapsed.Should().BeTrue();
        audit.Requests.Should().ContainSingle().Which.Action.Should().Be(NavigationAuditAction.Expand);
        browser.PersistedDesktopPreferences.Should().BeEmpty();

        await state.ToggleAsync();

        state.CompactOverlayOpen.Should().BeFalse();
        state.DesktopCollapsed.Should().BeTrue();
        audit.Requests.Should().HaveCount(2);
        audit.Requests[1].Action.Should().Be(NavigationAuditAction.Collapse);
        browser.PersistedDesktopPreferences.Should().BeEmpty(
            "compact overlay state must never become the desktop session preference");
    }

    [Fact]
    public async Task BreakpointChanges_CloseOverlayAndRetainDesktopPreference()
    {
        var browser = new FakeNavigationShellBrowser(
            new NavigationShellBrowserSnapshot(DesktopCollapsed: true, IsCompact: false));
        var audit = new FakeNavigationAuditClient();
        await using var state = new NavigationShellState(browser, audit);
        await state.InitializeAsync();

        await browser.RaiseBreakpointChangedAsync(isCompact: true);
        await state.ToggleAsync();
        state.CompactOverlayOpen.Should().BeTrue();

        await browser.RaiseBreakpointChangedAsync(isCompact: false);

        state.IsCompact.Should().BeFalse();
        state.CompactOverlayOpen.Should().BeFalse();
        state.DesktopCollapsed.Should().BeTrue();
        browser.PersistedDesktopPreferences.Should().BeEmpty();
        audit.Requests.Should().ContainSingle(
            "responsive transitions are reconciliation, not user-triggered audit actions");
    }

    [Fact]
    public async Task OnRouteChanged_ClosesCompactOverlayWithoutChangingDesktopPreference()
    {
        var browser = new FakeNavigationShellBrowser(
            new NavigationShellBrowserSnapshot(DesktopCollapsed: false, IsCompact: true));
        var audit = new FakeNavigationAuditClient();
        await using var state = new NavigationShellState(browser, audit);
        await state.InitializeAsync();
        await state.ToggleAsync();
        state.CompactOverlayOpen.Should().BeTrue();

        state.OnRouteChanged();

        state.CompactOverlayOpen.Should().BeFalse();
        state.DesktopCollapsed.Should().BeFalse();
        browser.PersistedDesktopPreferences.Should().BeEmpty();
        audit.Requests.Should().ContainSingle(
            "route reconciliation must not emit another user-action audit");
    }

    [Fact]
    public async Task DisposeAsync_ReleasesBrowserCallbacksAndIsIdempotent()
    {
        var browser = new FakeNavigationShellBrowser(
            new NavigationShellBrowserSnapshot(DesktopCollapsed: false, IsCompact: false));
        var audit = new FakeNavigationAuditClient();
        var state = new NavigationShellState(browser, audit);
        await state.InitializeAsync();

        await state.DisposeAsync();
        await state.DisposeAsync();
        await browser.RaiseBreakpointChangedAsync(isCompact: true);
        await browser.RaiseEscapePressedAsync();

        browser.DisposeCalls.Should().Be(1);
        browser.HasActiveCallbacks.Should().BeFalse();
        state.IsCompact.Should().BeFalse();
        state.DesktopCollapsed.Should().BeFalse();
        state.CompactOverlayOpen.Should().BeFalse();
        audit.Requests.Should().BeEmpty();
    }

    private sealed class FakeNavigationShellBrowser(
        NavigationShellBrowserSnapshot initialState) : INavigationShellBrowser
    {
        private Func<bool, ValueTask>? _breakpointChanged;
        private Func<ValueTask>? _escapePressed;
        private bool _disposed;

        public List<bool> PersistedDesktopPreferences { get; } = [];
        public int DisposeCalls { get; private set; }
        public bool HasActiveCallbacks => _breakpointChanged is not null || _escapePressed is not null;

        public ValueTask<NavigationShellBrowserSnapshot> InitializeAsync(
            Func<bool, ValueTask> breakpointChanged,
            Func<ValueTask> escapePressed,
            CancellationToken cancellationToken = default)
        {
            _breakpointChanged = breakpointChanged;
            _escapePressed = escapePressed;
            return ValueTask.FromResult(initialState);
        }

        public ValueTask PersistDesktopCollapsedAsync(
            bool collapsed,
            CancellationToken cancellationToken = default)
        {
            PersistedDesktopPreferences.Add(collapsed);
            return ValueTask.CompletedTask;
        }

        public ValueTask RaiseBreakpointChangedAsync(bool isCompact)
            => _breakpointChanged?.Invoke(isCompact) ?? ValueTask.CompletedTask;

        public ValueTask RaiseEscapePressedAsync()
            => _escapePressed?.Invoke() ?? ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            _disposed = true;
            DisposeCalls++;
            _breakpointChanged = null;
            _escapePressed = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeNavigationAuditClient : INavigationAuditClient
    {
        public List<NavigationAuditRequest> Requests { get; } = [];
        public Func<NavigationAuditRequest, Task<NavigationAuditResult>>? OnRecord { get; set; }

        public Task<NavigationAuditResult> RecordNavigationAsync(
            NavigationAuditRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return OnRecord?.Invoke(request)
                ?? Task.FromResult(new NavigationAuditResult(request.CorrelationId));
        }
    }
}