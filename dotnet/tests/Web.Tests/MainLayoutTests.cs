using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Web.Client.Layout;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class MainLayoutTests : BunitContext
{
    private const string OrganizationId = "30000000-0000-4000-8000-000000000001";
    private const string DepartmentId = "40000000-0000-4000-8000-000000000001";

    [Fact]
    public void Toggle_IsASingleControlRenderedOutsideTheCollapsibleRegion()
    {
        var browser = new StubNavigationShellBrowser(new(DesktopCollapsed: false, IsCompact: false));
        var cut = RenderLayout(browser, new StubNavigationAuditClient());

        var toggles = cut.FindAll("[data-testid='navigation-toggle']");
        toggles.Should().ContainSingle("the shell exposes exactly one navigation toggle");

        var toggle = cut.Find("[data-testid='navigation-toggle']");
        var controlledId = toggle.GetAttribute("aria-controls");
        controlledId.Should().NotBeNullOrWhiteSpace();

        var region = cut.Find($"#{controlledId}");
        region.QuerySelector("[data-testid='navigation-toggle']").Should()
            .BeNull("the toggle must stay reachable when the region collapses to zero width");
    }

    [Fact]
    public async Task Toggle_AnnouncesStateThroughAriaExpandedAndItsAccessibleName()
    {
        var browser = new StubNavigationShellBrowser(new(DesktopCollapsed: false, IsCompact: false));
        var cut = RenderLayout(browser, new StubNavigationAuditClient());

        var toggle = cut.Find("[data-testid='navigation-toggle']");
        toggle.GetAttribute("aria-expanded").Should().Be("true");
        AccessibleName(cut).Should().Be("Collapse navigation");

        await cut.InvokeAsync(() => cut.Find("[data-testid='navigation-toggle']").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='navigation-toggle']").GetAttribute("aria-expanded").Should().Be("false");
            AccessibleName(cut).Should().Be("Expand navigation");
        });
    }

    [Fact]
    public async Task Toggle_AuditFailure_RetainsPriorStateAndShowsRetryableNotification()
    {
        var browser = new StubNavigationShellBrowser(new(DesktopCollapsed: false, IsCompact: false));
        var audit = new StubNavigationAuditClient
        {
            OnRecord = _ => Task.FromException<NavigationAuditResult>(
                new InvalidOperationException("Audit storage is unavailable.")),
        };
        var cut = RenderLayout(browser, audit);

        await cut.InvokeAsync(() => cut.Find("[data-testid='navigation-toggle']").Click());

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[role='alert']");
            alert.TextContent.Should().NotBeNullOrWhiteSpace();
            cut.Find("[data-testid='navigation-toggle']").GetAttribute("aria-expanded").Should().Be("true");
        });
        cut.FindAll("[data-action='retry-navigation']").Should()
            .NotBeEmpty("an audit failure must be recoverable without a page reload");
        browser.PersistedDesktopPreferences.Should().BeEmpty();
    }

    [Fact]
    public async Task Toggle_KeepsTheRoutedBodyMountedAcrossCollapseAndExpansion()
    {
        var browser = new StubNavigationShellBrowser(new(DesktopCollapsed: false, IsCompact: false));
        var probe = new BodyProbeState();
        var cut = RenderLayout(browser, new StubNavigationAuditClient(), probe);

        probe.InitializationCount.Should().Be(1);

        await cut.InvokeAsync(() => cut.Find("[data-testid='navigation-toggle']").Click());
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='navigation-toggle']").GetAttribute("aria-expanded").Should().Be("false"));
        await cut.InvokeAsync(() => cut.Find("[data-testid='navigation-toggle']").Click());
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='navigation-toggle']").GetAttribute("aria-expanded").Should().Be("true"));

        cut.Find("[data-testid='body-probe']").Should().NotBeNull();
        probe.InitializationCount.Should().Be(1, "collapsing navigation must never remount page content");
    }

    [Fact]
    public async Task CompactOverlay_ClosesOnBackdropActivation()
    {
        var browser = new StubNavigationShellBrowser(new(DesktopCollapsed: false, IsCompact: true));
        var cut = RenderLayout(browser, new StubNavigationAuditClient());

        await cut.InvokeAsync(() => cut.Find("[data-testid='navigation-toggle']").Click());
        cut.WaitForElement("[data-testid='navigation-backdrop']");

        await cut.InvokeAsync(() => cut.Find("[data-testid='navigation-backdrop']").Click());

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='navigation-backdrop']").Should().BeEmpty());
        browser.PersistedDesktopPreferences.Should()
            .BeEmpty("the transient overlay is never a persisted desktop preference");
    }

    [Fact]
    public async Task CompactOverlay_ClosesOnEscape()
    {
        var browser = new StubNavigationShellBrowser(new(DesktopCollapsed: false, IsCompact: true));
        var cut = RenderLayout(browser, new StubNavigationAuditClient());

        await cut.InvokeAsync(() => cut.Find("[data-testid='navigation-toggle']").Click());
        cut.WaitForElement("[data-testid='navigation-backdrop']");

        await cut.InvokeAsync(async () => await browser.RaiseEscapePressedAsync());

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='navigation-backdrop']").Should().BeEmpty());
    }

    [Fact]
    public void NavigationLinks_AreLimitedToTheAuthorizedSurfaceForTheSignedInActor()
    {
        var browser = new StubNavigationShellBrowser(new(DesktopCollapsed: false, IsCompact: false));
        var cut = RenderLayout(browser, new StubNavigationAuditClient(), role: "organization_admin");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Access management"));
        cut.Markup.Should().Contain("Organizations");
        cut.Markup.Should().NotContain("Failure Queue");
    }

    private static string AccessibleName(IRenderedComponent<MainLayout> cut)
    {
        var toggle = cut.Find("[data-testid='navigation-toggle']");
        return (toggle.GetAttribute("aria-label") ?? toggle.TextContent).Trim();
    }

    private IRenderedComponent<MainLayout> RenderLayout(
        StubNavigationShellBrowser browser,
        StubNavigationAuditClient audit,
        BodyProbeState? probe = null,
        string role = "admin")
    {
        var configuration = new PublicAuthConfiguration(
            "entra",
            TenantId: "10000000-0000-4000-8000-000000000001",
            ClientId: "client-id",
            Authority: "https://login.microsoftonline.com/tenant",
            ApiScope: "api://talentmatch/access_as_user",
            ApiBaseAddress: "http://localhost/");
        var client = new HttpClient(new CurrentUserHandler(role))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        Services.AddSingleton(configuration);
        Services.AddSingleton(new ApiClient(client, configuration));
        Services.AddSingleton<INavigationShellBrowser>(browser);
        Services.AddSingleton<INavigationAuditClient>(audit);
        Services.AddScoped(provider => new NavigationShellState(
            provider.GetRequiredService<INavigationShellBrowser>(),
            provider.GetRequiredService<INavigationAuditClient>()));

        var state = probe ?? new BodyProbeState();
        return Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder =>
            {
                builder.OpenComponent<BodyProbe>(0);
                builder.AddComponentParameter(1, nameof(BodyProbe.State), state);
                builder.CloseComponent();
            }));
    }

    private sealed class BodyProbeState
    {
        public int InitializationCount { get; set; }
    }

    private sealed class BodyProbe : ComponentBase
    {
        [Parameter] public BodyProbeState State { get; set; } = default!;

        protected override void OnInitialized() => State.InitializationCount++;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-testid", "body-probe");
            builder.AddContent(2, "Routed page content");
            builder.CloseElement();
        }
    }

    private sealed class StubNavigationShellBrowser(NavigationShellBrowserSnapshot initialState)
        : INavigationShellBrowser
    {
        private Func<bool, ValueTask>? _breakpointChanged;
        private Func<ValueTask>? _escapePressed;

        public List<bool> PersistedDesktopPreferences { get; } = [];

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
            _breakpointChanged = null;
            _escapePressed = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubNavigationAuditClient : INavigationAuditClient
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

    private sealed class CurrentUserHandler(string role) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!path.EndsWith("/api/auth/me", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
                {
                    Content = new StringContent(string.Empty),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Authorization(role), Encoding.UTF8, "application/json"),
            });
        }

        private static string Authorization(string role) => JsonSerializer.Serialize(new
        {
            userId = "user-1",
            tenantId = "10000000-0000-4000-8000-000000000001",
            objectId = "10000000-0000-4000-8000-000000000002",
            username = "actor@example.com",
            fullName = "Signed In Actor",
            email = "actor@example.com",
            globalRole = role == "admin" ? "admin" : null,
            authorizationVersion = 3,
            memberships = new[]
            {
                new
                {
                    organizationId = OrganizationId,
                    organizationName = "Primary",
                    defaultDepartmentId = DepartmentId,
                    departments = new[] { new { departmentId = DepartmentId, departmentName = "Engineering" } },
                },
            },
            authorizations = role == "admin"
                ? Array.Empty<object>()
                : new[]
                {
                    new
                    {
                        role,
                        roleLabel = "Organization Admin",
                        organizationId = OrganizationId,
                        departmentId = (string?)null,
                        assignmentSource = "delegated",
                    },
                },
            tokenIssuedAt = "2026-08-01T10:00:00Z",
            refreshRequiredAt = "2026-08-01T10:15:00Z",
        });
    }
}
