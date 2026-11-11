using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Bunit.TestDoubles;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class BulkScoringSourceConnectionUxTests : BunitContext
{
    private const string FolderUrl =
        "https://contoso.sharepoint.com/sites/hr/Shared%20Documents/Candidates";
    private const string ConnectionId = "11111111-1111-1111-1111-111111111111";
    private const string RequestId = "22222222-2222-2222-2222-222222222222";
    private const string RetryRequestId = "33333333-3333-3333-3333-333333333333";
    private const string AuthorizationUrl =
        "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=test-client&state=opaque";
    private const string AdminConsentUrl =
        "https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id=test-client&state=opaque-admin";

    [Fact]
    public void Source_permission_is_explained_as_separate_from_product_sign_in()
    {
        var cut = Open(new SourceConnectionHandler());

        var message = Flat(cut.Find("[data-testid=source-consent-message]"));
        message.Should().Contain("separate from your product sign-in")
            .And.Contain("read-only")
            .And.Contain("work or school");
        cut.Markup.Should().NotContain("application-only");
    }

    [Fact]
    public void Pasted_folder_identifies_the_source_tenant_account_required_at_Microsoft()
    {
        var cut = Open(new SourceConnectionHandler());

        cut.Find("#bulk-folder-url").Input(FolderUrl);

        var guidance = Flat(cut.Find("[data-testid=source-tenant-guidance]"));
        guidance.Should().Contain("normal account from contoso.sharepoint.com")
            .And.Contain("Administrator permissions are not required")
            .And.Contain("Do not use your TalentMatch sign-in account")
            .And.Contain("source tenant");
        cut.Find("[data-testid=connect-source]").TextContent.Should()
            .Contain("source account");
    }

    [Fact]
    public void Starting_a_connection_redirects_to_Microsoft_and_does_not_read_the_folder()
    {
        var handler = new SourceConnectionHandler();
        var cut = Open(handler);

        StartConnection(cut);

        cut.WaitForAssertion(() =>
        {
            handler.Requests.Should().ContainSingle(request =>
                request.Method == "POST" &&
                request.Path == "/api/jobs/job-1/source-connections" &&
                request.Body.Contains(FolderUrl, StringComparison.Ordinal));
            Services.GetRequiredService<NavigationManager>().Uri.Should().Be(AuthorizationUrl);
        });
        handler.Requests.Should().NotContain(request =>
            request.Path.Contains("/preview", StringComparison.Ordinal) ||
            request.Path.Contains("/draft-items", StringComparison.Ordinal));
    }

    [Fact]
    public void Returning_from_user_consent_resumes_the_pending_folder_journey()
    {
        var handler = new SourceConnectionHandler();

        var cut = OpenResumed(handler);

        cut.WaitForAssertion(() =>
        {
            handler.Requests.Should().Contain(request =>
                request.Method == "GET" &&
                request.Path == $"/api/source-connections/{ConnectionId}");
            handler.Requests.Should().Contain(request =>
                request.Method == "POST" &&
                request.Path == $"/api/source-connections/{ConnectionId}/preview");
            cut.Find("[data-testid=draft-review]").TextContent.Should().Contain("Candidates");
        });
        Services.GetRequiredService<NavigationManager>().Uri.Should()
            .StartWith("http://localhost/jobs/job-1");
    }

    [Fact]
    public void Returning_from_retry_consent_starts_a_new_request_with_the_verified_connection()
    {
        var handler = new SourceConnectionHandler();
        RegisterApi(handler);
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"http://localhost/jobs/job-1?retryBulkRequestId={RetryRequestId}&sourceConnectionId={ConnectionId}");

        using var cut = RenderDialog(null, null, RetryRequestId);

        cut.WaitForAssertion(() =>
        {
            handler.Requests.Should().Contain(request =>
                request.Method == "POST"
                && request.Path == $"/api/bulk-scoring-requests/{RetryRequestId}/retry-failed"
                && request.Body.Contains(ConnectionId, StringComparison.Ordinal));
            cut.Find("[data-testid=bulk-scoring-accepted]").Should().NotBeNull();
        });
        handler.Requests.Should().NotContain(request =>
            request.Path == $"/api/source-connections/{ConnectionId}/preview");
    }

    [Fact]
    public void Administrator_consent_is_actionable_and_does_not_start_discovery()
    {
        var handler = new SourceConnectionHandler
        {
            ConnectionStatusResponse = () => Json(HttpStatusCode.OK, new
            {
                connectionId = ConnectionId,
                status = "authorizing",
                expiresAt = DateTimeOffset.Parse("2030-01-02T03:04:05Z"),
                adminConsentRequired = true,
                adminConsentUrl = AdminConsentUrl,
            }),
        };

        var cut = OpenResumed(handler);

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find("[data-testid=admin-consent-required]");
            message.GetAttribute("role").Should().Be("status");
            Flat(message).Should().Contain("administrator consent is required")
                .And.Contain("No folder content has been read");
            cut.Find("[data-testid=request-admin-consent]")
                .GetAttribute("href").Should().Be(AdminConsentUrl);
        });
        handler.Requests.Should().NotContain(request =>
            request.Path.Contains("/preview", StringComparison.Ordinal) ||
            request.Path.Contains("/draft-items", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_paging_never_discovers_more_and_discovery_has_a_separate_explicit_action()
    {
        var handler = new SourceConnectionHandler();
        var cut = OpenResumed(handler);
        cut.WaitForElement("[data-testid=draft-review]");
        var discoveryCalls = handler.Requests.Count(IsDiscoverMore);

        cut.Find("[data-testid=review-next]").Click();

        cut.WaitForAssertion(() => handler.Requests.Should().Contain(request =>
            request.Method == "GET" &&
            request.Path.Contains($"/api/bulk-scoring-requests/{RequestId}/draft-items", StringComparison.Ordinal) &&
            request.Path.Contains("reviewCursor=cursor-2", StringComparison.Ordinal)));
        handler.Requests.Count(IsDiscoverMore).Should().Be(discoveryCalls,
            "paging through the frozen review must not continue discovery");
        cut.Find("[data-testid=review-total]").TextContent.Should().Contain("125");

        cut.Find("[data-testid=discover-more]").Click();

        cut.WaitForAssertion(() => handler.Requests.Count(IsDiscoverMore).Should().Be(discoveryCalls + 1));
        cut.Find("[data-testid=review-total]").TextContent.Should().Contain("150");
    }

    [Fact]
    public void Review_displays_the_frozen_request_limits_and_configuration_choices()
    {
        var cut = OpenResumed(new SourceConnectionHandler());

        cut.WaitForAssertion(() =>
        {
            var frozen = Flat(cut.Find("[data-testid=frozen-request-configuration]"));
            frozen.Should().Contain("Frozen for this request")
                .And.Contain("30,000 files")
                .And.Contain("8 MiB per file")
                .And.Contain("50 GiB total")
                .And.Contain("1,000 files per discovery step");
        });
        cut.Find("[data-testid=convert-raster]").HasAttribute("checked").Should().BeFalse();
        cut.Find("[data-testid=allow-duplicates]").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public async Task Connection_loading_and_failure_are_accessible_and_focus_the_error()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SourceConnectionHandler
        {
            BeginGate = gate.Task,
            BeginResponse = () => Problem(
                HttpStatusCode.TooManyRequests,
                "source_connection_rate_limited",
                "Too many source connection attempts were made. Try again later."),
        };
        var cut = Open(handler);

        StartConnection(cut);

        var region = cut.Find("[data-testid=source-connection-region]");
        region.GetAttribute("aria-busy").Should().Be("true");
        cut.Find("[data-testid=connect-source]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=source-loading]").GetAttribute("role").Should().Be("status");

        gate.SetResult();

        await cut.WaitForAssertionAsync(() =>
        {
            var error = cut.Find("[data-testid=source-error]");
            error.GetAttribute("role").Should().Be("alert");
            error.GetAttribute("tabindex").Should().Be("-1");
            error.TextContent.Should().Contain("Try again later");
            JSInterop.Invocations.Should().Contain(invocation =>
                invocation.Identifier == "Blazor._internal.domWrapper.focus");
        });
    }

    [Fact]
    public void A_stale_product_session_offers_sign_in_without_losing_the_folder()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Open(new SourceConnectionHandler
        {
            BeginResponse = StaleSession,
        });

        StartConnection(cut);

        cut.WaitForAssertion(() =>
        {
            Flat(cut.Find("[data-testid=source-error]")).Should().Contain("TalentMatch")
                .And.Contain("Sign in again");
            cut.Find("[data-testid=refresh-product-session]").TextContent.Should().Contain("Sign in again");
            cut.Find("#bulk-folder-url").GetAttribute("value").Should().Be(FolderUrl);
        });
    }

    [Fact]
    public void An_interactive_token_requirement_offers_product_sign_in_instead_of_a_generic_error()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Open(new SourceConnectionHandler
        {
            BeginResponse = () =>
            {
                var navigation = Services.GetRequiredService<NavigationManager>();
                var result = new AccessTokenResult(
                    AccessTokenResultStatus.RequiresRedirect,
                    new AccessToken { Value = "", Expires = DateTimeOffset.MinValue, GrantedScopes = [] },
                    "authentication/login",
                    new InteractiveRequestOptions { Interaction = InteractionType.GetToken, ReturnUrl = navigation.Uri });
                throw new AccessTokenNotAvailableException(navigation, result, ["api://test/access_as_user"]);
            },
        });

        StartConnection(cut);

        cut.WaitForAssertion(() =>
        {
            Flat(cut.Find("[data-testid=source-error]")).Should().Contain("TalentMatch");
            cut.Find("[data-testid=refresh-product-session]").Should().NotBeNull();
            cut.Find("#bulk-folder-url").GetAttribute("value").Should().Be(FolderUrl);
        });
    }

    [Fact]
    public void Other_authorization_denials_keep_their_message_without_offering_session_recovery()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Open(new SourceConnectionHandler
        {
            BeginResponse = () => Json(HttpStatusCode.Unauthorized, new
            {
                error = "wrong_tenant",
                message = "Sign in with an account from the configured organization.",
            }),
        });

        StartConnection(cut);

        cut.WaitForAssertion(() => Flat(cut.Find("[data-testid=source-error]")).Should()
            .Contain("configured organization"));
        cut.FindAll("[data-testid=refresh-product-session]").Should().BeEmpty();
    }

    [Fact]
    public void Canceling_session_recovery_closes_the_dialog_without_starting_work()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new SourceConnectionHandler { BeginResponse = StaleSession };
        var closed = 0;
        var cut = Open(handler, onClose: () => closed++);
        StartConnection(cut);

        cut.WaitForElement("[data-testid=source-error]").QuerySelectorAll("button")
            .Single(button => button.TextContent.Trim() == "Cancel").Click();

        closed.Should().Be(1);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void Signing_in_again_saves_the_folder_in_this_tab_and_returns_to_the_same_job()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Open(new SourceConnectionHandler { BeginResponse = StaleSession });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("http://localhost/jobs/job-1");
        StartConnection(cut);
        cut.WaitForElement("[data-testid=refresh-product-session]").Click();

        cut.WaitForAssertion(() =>
        {
            navigation.Uri.Should().Be("http://localhost/authentication/login");
            var saved = JSInterop.Invocations.Single(invocation => invocation.Identifier == "sessionStorage.setItem");
            saved.Arguments[0].Should().Be(ResumeStorageKey);
            using var state = JsonDocument.Parse((string)saved.Arguments[1]!);
            state.RootElement.GetProperty("FolderUrl").GetString().Should().Be(FolderUrl);
            var authenticationState = Services.GetRequiredService<BunitNavigationManager>()
                .History.First().Options.HistoryEntryState;
            authenticationState.Should().Contain("resumeBulkScoring")
                .And.Contain("jobs/job-1")
                .And.NotContain("sharepoint.com");
        });
    }

    [Fact]
    public void Signing_in_again_keeps_retry_intent_in_the_return_route()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        RegisterApi(new SourceConnectionHandler { BeginResponse = StaleSession });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/jobs/job-1");
        var cut = RenderDialog(null, null, RetryRequestId);
        StartConnection(cut);

        cut.WaitForElement("[data-testid=refresh-product-session]").Click();

        Services.GetRequiredService<BunitNavigationManager>().History.First().Options.HistoryEntryState.Should()
            .Contain("retryBulkRequestId").And.Contain(RetryRequestId);
    }

    [Fact]
    public void Authentication_failure_during_confirmation_preserves_the_existing_draft_and_review_choices()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new SourceConnectionHandler { ConfirmResponse = StaleSession };
        var cut = OpenResumed(handler);
        cut.WaitForElement("[data-testid=draft-review]");
        cut.Find("[data-testid=convert-raster]").Change(true);
        cut.Find("[data-testid=allow-duplicates]").Change(true);
        cut.Find("[data-testid=confirm-bulk-scoring]").Click();

        cut.WaitForElement("[data-testid=refresh-product-session]").Click();

        cut.WaitForAssertion(() =>
        {
            var saved = JSInterop.Invocations.Single(invocation => invocation.Identifier == "sessionStorage.setItem");
            using var state = JsonDocument.Parse((string)saved.Arguments[1]!);
            state.RootElement.GetProperty("ConnectionId").GetString().Should().Be(ConnectionId);
            state.RootElement.GetProperty("RequestId").GetString().Should().Be(RequestId);
            state.RootElement.GetProperty("ConvertToRasterPdf").GetBoolean().Should().BeTrue();
            state.RootElement.GetProperty("AllowDuplicates").GetBoolean().Should().BeTrue();
        });
        handler.Requests.Count(request => request.Path.EndsWith("/confirm", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void Product_sign_in_return_restores_the_folder_without_starting_another_connection()
    {
        var handler = new SourceConnectionHandler();
        var cut = OpenAfterProductSignIn(handler, new
        {
            FolderUrl,
            ConnectionId = (string?)null,
            RequestId = (string?)null,
            ConvertToRasterPdf = false,
            AllowDuplicates = false,
        });

        cut.WaitForAssertion(() => cut.Find("#bulk-folder-url").GetAttribute("value").Should().Be(FolderUrl));
        handler.Requests.Should().BeEmpty("restoring a form must not perform a mutation");
        JSInterop.Invocations.Should().Contain(invocation =>
            invocation.Identifier == "sessionStorage.removeItem" && Equals(invocation.Arguments[0], ResumeStorageKey));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Be("http://localhost/jobs/job-1");
    }

    [Fact]
    public void Product_sign_in_return_reloads_the_existing_review_without_creating_or_confirming_a_draft()
    {
        var handler = new SourceConnectionHandler();
        var cut = OpenAfterProductSignIn(handler, new
        {
            FolderUrl,
            ConnectionId,
            RequestId,
            ConvertToRasterPdf = true,
            AllowDuplicates = true,
        });

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=draft-review]").Should().NotBeNull();
            cut.Find("[data-testid=convert-raster]").HasAttribute("checked").Should().BeTrue();
            cut.Find("[data-testid=allow-duplicates]").HasAttribute("checked").Should().BeTrue();
            handler.Requests.Should().Contain(request =>
                request.Method == "GET" && request.Path == $"/api/bulk-scoring-requests/{RequestId}");
        });
        handler.Requests.Should().OnlyContain(request => request.Method == "GET",
            "preview creation, discovery, confirmation and retry must not be replayed after interactive sign-in");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-json")]
    public void Missing_or_corrupt_resume_state_is_actionable_and_does_not_start_work(string? state)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("sessionStorage.getItem", ResumeStorageKey).SetResult(state);
        var handler = new SourceConnectionHandler();
        RegisterApi(handler);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/jobs/job-1?resumeBulkScoring=1");

        var cut = RenderDialog(null, null);

        cut.WaitForAssertion(() => Flat(cut.Find("[data-testid=source-error]")).Should()
            .Contain("folder link").And.Contain("again"));
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Unavailable_tab_storage_keeps_the_form_open_and_explains_how_to_continue()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("sessionStorage.setItem", _ => true)
            .SetException(new JSException("Storage is unavailable."));
        var cut = Open(new SourceConnectionHandler { BeginResponse = StaleSession });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("http://localhost/jobs/job-1");
        StartConnection(cut);

        cut.WaitForElement("[data-testid=refresh-product-session]").Click();

        cut.WaitForAssertion(() =>
        {
            navigation.Uri.Should().Be("http://localhost/jobs/job-1");
            Flat(cut.Find("[data-testid=source-error]")).Should().Contain("Copy")
                .And.Contain("folder link");
            cut.Find("#bulk-folder-url").GetAttribute("value").Should().Be(FolderUrl);
        });
    }

    [Fact]
    public void Confirmation_sends_explicit_choices_and_the_expected_draft_version()
    {
        var handler = new SourceConnectionHandler();
        var started = 0;
        var cut = OpenResumed(handler, onStarted: () => started++);
        cut.WaitForElement("[data-testid=draft-review]");

        cut.Find("[data-testid=convert-raster]").Change(true);
        cut.Find("[data-testid=allow-duplicates]").Change(true);
        cut.Find("[data-testid=confirm-bulk-scoring]").Click();

        cut.WaitForAssertion(() =>
        {
            var confirm = handler.Requests.Single(request =>
                request.Method == "POST" &&
                request.Path == $"/api/bulk-scoring-requests/{RequestId}/confirm");
            using var body = JsonDocument.Parse(confirm.Body);
            body.RootElement.GetProperty("convertToRasterPdf").GetBoolean().Should().BeTrue();
            body.RootElement.GetProperty("allowDuplicates").GetBoolean().Should().BeTrue();
            body.RootElement.GetProperty("expectedConcurrencyVersion").GetInt32().Should().Be(7);
            cut.Find("[data-testid=bulk-scoring-accepted]").GetAttribute("role").Should().Be("status");
            started.Should().Be(1);
        });
        Flat(cut.Find("[data-testid=bulk-scoring-accepted]"))
            .Should().Contain("configuration version config-v17")
            .And.Contain("continues in the background");
    }

    [Fact]
    public void Abandoning_review_discards_the_draft_and_closes_without_confirming()
    {
        var handler = new SourceConnectionHandler();
        var closed = 0;
        var cut = OpenResumed(handler, onClose: () => closed++);
        cut.WaitForElement("[data-testid=draft-review]");

        cut.Find("[data-testid=abandon-source-journey]").Click();

        cut.WaitForAssertion(() =>
        {
            handler.Requests.Should().Contain(request =>
                request.Method == "DELETE" &&
                request.Path == $"/api/bulk-scoring-requests/{RequestId}");
            closed.Should().Be(1);
        });
        handler.Requests.Should().NotContain(request => request.Path.EndsWith("/confirm", StringComparison.Ordinal));
        cut.Markup.Should().NotContain(FolderUrl);
    }

    private IRenderedComponent<BulkScoringRequestDialog> Open(
        SourceConnectionHandler handler,
        Action? onClose = null,
        Action? onStarted = null)
    {
        RegisterApi(handler);
        return RenderDialog(onClose, onStarted);
    }

    private IRenderedComponent<BulkScoringRequestDialog> RenderDialog(
        Action? onClose,
        Action? onStarted,
        string? retryOfRequestId = null)
    {
        return Render<BulkScoringRequestDialog>(parameters => parameters
            .Add(component => component.JobId, "job-1")
            .Add(component => component.RetryOfRequestId, retryOfRequestId)
            .Add(component => component.OnClose,
                EventCallback.Factory.Create(this, () => onClose?.Invoke()))
            .Add(component => component.OnStarted,
                EventCallback.Factory.Create(this, () => onStarted?.Invoke())));
    }

    private IRenderedComponent<BulkScoringRequestDialog> OpenResumed(
        SourceConnectionHandler handler,
        Action? onClose = null,
        Action? onStarted = null)
    {
        RegisterApi(handler);
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/jobs/job-1?sourceConnectionId={ConnectionId}");
        return RenderDialog(onClose, onStarted);
    }

    private void RegisterApi(SourceConnectionHandler handler)
    {
        Services.AddSingleton(new ApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        }));
    }

    private const string ResumeStorageKey = "talentMatch.bulkSource.resume.job-1";

    private IRenderedComponent<BulkScoringRequestDialog> OpenAfterProductSignIn(
        SourceConnectionHandler handler,
        object state)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("sessionStorage.getItem", ResumeStorageKey)
            .SetResult(JsonSerializer.Serialize(state));
        RegisterApi(handler);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/jobs/job-1?resumeBulkScoring=1");
        return RenderDialog(null, null);
    }

    private static HttpResponseMessage StaleSession() => Json(HttpStatusCode.Unauthorized, new
    {
        error = "token_stale",
        message = "Your session must be refreshed before continuing.",
        correlationId = "session-correlation",
    });

    private static void StartConnection(IRenderedComponent<BulkScoringRequestDialog> cut)
    {
        cut.Find("#bulk-folder-url").Input(FolderUrl);
        cut.Find("[data-testid=connect-source]").Click();
    }

    private static bool IsDiscoverMore(RequestRecord request) =>
        request.Method == "POST" &&
        request.Path == $"/api/bulk-scoring-requests/{RequestId}/discover-more";

    private static string Flat(AngleSharp.Dom.IElement element) =>
        System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();

    private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail) =>
        new(status)
        {
            Content = JsonContent.Create(new
            {
                type = "about:blank",
                title = "The source connection could not be started.",
                status = (int)status,
                detail,
                code,
                correlationId = "corr-source-1",
            }),
        };

    private sealed record RequestRecord(string Method, string Path, string Body);

    private sealed class SourceConnectionHandler : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];
        public Task? BeginGate { get; init; }
        public Func<HttpResponseMessage> ConfirmResponse { get; init; } = () => Confirmed();
        public Func<HttpResponseMessage> BeginResponse { get; init; } = () => Json(HttpStatusCode.Created, new
        {
            connectionId = ConnectionId,
            status = "authorizing",
            authorizationUrl = AuthorizationUrl,
            expiresAt = DateTimeOffset.Parse("2030-01-02T03:04:05Z"),
        });
        public Func<HttpResponseMessage> ConnectionStatusResponse { get; init; } = () => Json(HttpStatusCode.OK, new
        {
            connectionId = ConnectionId,
            status = "authorized",
            expiresAt = DateTimeOffset.Parse("2030-01-02T03:04:05Z"),
            sourceKind = "sharepoint",
            sourceAccountDisplay = "Recruiter (r***@contoso.com)",
            folderName = (string?)null,
            adminConsentRequired = false,
            adminConsentUrl = (string?)null,
            safeFailureCode = (string?)null,
        });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method.Method, path, body));

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/api/jobs/job-1/source-connections")
            {
                if (BeginGate is not null)
                    await BeginGate.WaitAsync(cancellationToken);
                return BeginResponse();
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == $"/api/source-connections/{ConnectionId}")
                return ConnectionStatusResponse();

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == $"/api/source-connections/{ConnectionId}/preview")
                return Preview(total: 125, status: "more_available", reviewVersion: 3);

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == $"/api/bulk-scoring-requests/{RequestId}/draft-items")
            {
                var secondPage = path.Contains("reviewCursor=cursor-2", StringComparison.Ordinal);
                return DraftPage(secondPage);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == $"/api/bulk-scoring-requests/{RequestId}")
                return Confirmed(draft: true);

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == $"/api/bulk-scoring-requests/{RequestId}/discover-more")
                return Preview(total: 150, status: "complete", reviewVersion: 4);

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == $"/api/bulk-scoring-requests/{RequestId}/confirm")
                return ConfirmResponse();

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == $"/api/bulk-scoring-requests/{RetryRequestId}/retry-failed")
                return Confirmed();

            if (request.Method == HttpMethod.Delete &&
                (request.RequestUri?.AbsolutePath == $"/api/bulk-scoring-requests/{RequestId}" ||
                 request.RequestUri?.AbsolutePath == $"/api/source-connections/{ConnectionId}"))
                return new(HttpStatusCode.NoContent);

            return Problem(HttpStatusCode.NotFound, "not_found", "The requested test route was not found.");
        }

        private static HttpResponseMessage Preview(int total, string status, int reviewVersion) =>
            Json(HttpStatusCode.Created, new
            {
                requestId = RequestId,
                connectionId = ConnectionId,
                sourceKind = "sharepoint",
                sourceAccountDisplay = "Recruiter (r***@contoso.com)",
                folderName = "Candidates",
                authorizationState = "pending",
                discoveryStatus = status,
                reviewSnapshotVersion = reviewVersion,
                concurrencyVersion = 7,
                counts = Counts(total),
                excludedByReason = new Dictionary<string, int> { ["unsupported_type"] = 2 },
                entriesScanned = total + 2,
                totalEligibleBytes = total * 1024L,
                limits = new
                {
                    maxFilesPerRequest = 30_000,
                    maxBytesPerFile = 8_388_608,
                    maxTotalBytes = 53_687_091_200,
                    discoveryStepMaxFiles = 1_000,
                },
                draftItemsPath = $"/api/bulk-scoring-requests/{RequestId}/draft-items",
            });

        private static HttpResponseMessage DraftPage(bool secondPage)
        {
            var start = secondPage ? 101 : 1;
            var count = secondPage ? 25 : 100;
            return Json(HttpStatusCode.OK, new
            {
                items = Enumerable.Range(start, count).Select(index => new
                {
                    itemId = $"item-{index:D3}",
                    fileName = $"candidate-{index:D3}.pdf",
                    relativePath = "/Candidates",
                    sizeBytes = 1024 + index,
                    mimeType = "application/pdf",
                }),
                totalItems = 125,
                reviewSnapshotVersion = 3,
                nextReviewCursor = secondPage ? null : "cursor-2",
            });
        }

        private static HttpResponseMessage Confirmed(bool draft = false) =>
            Json(draft ? HttpStatusCode.OK : HttpStatusCode.Created, new
            {
                requestId = RequestId,
                jobId = "job-1",
                status = draft ? "draft" : "active",
                outcome = (string?)null,
                sourceKind = "sharepoint",
                sourceAccountDisplay = "Recruiter (r***@contoso.com)",
                folderName = "Candidates",
                folderPath = "/Candidates",
                folderWebUrl = "https://contoso.sharepoint.com/safe/verified-folder",
                submittedBy = "Recruiter",
                submittedAt = DateTimeOffset.Parse("2029-04-05T06:07:08Z"),
                terminalAt = (DateTimeOffset?)null,
                convertToRasterPdf = true,
                allowDuplicates = true,
                cancelRequested = false,
                retryOfRequestId = (string?)null,
                acknowledged = false,
                counts = Counts(125),
                concurrencyVersion = draft ? 7 : 8,
                limits = new
                {
                    maxFilesPerRequest = 30_000,
                    maxBytesPerFile = 8_388_608,
                    maxTotalBytes = 53_687_091_200,
                    maxPagesPerDocument = 30,
                    discoveryStepFiles = 1_000,
                },
                jobConfigVersionId = "config-v17",
                capturedScoringConfigVersionId = "config-v17",
                currentScoringConfigVersionId = "config-v17",
                configChangedSinceAcceptance = false,
                discovery = draft ? new
                {
                    discoveryStatus = "more_available",
                    entriesScanned = 127,
                    stepCount = 1,
                    includedCount = 125,
                    includedBytes = 125 * 1024L,
                    includedCountIfRasterized = 125,
                    includedBytesIfRasterized = 125 * 1024L,
                    excludedCount = 2,
                    excludedByReason = new Dictionary<string, int> { ["unsupported_type"] = 2 },
                    inaccessibleItemCount = 0,
                } : null,
                accessModel = "delegated",
                authorizationState = "active",
                sourceTenantId = "masked",
            });

        private static object Counts(int discovered) => new
        {
            discovered,
            excluded = 2,
            unreadable = 0,
            waiting = discovered,
            importing = 0,
            converting = 0,
            scoring = 0,
            completed = 0,
            failed = 0,
            skipped = 0,
            cancelled = 0,
        };
    }
}
