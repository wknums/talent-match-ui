using System.Collections.Concurrent;
using System.Security.Cryptography;
using Azure.Core;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.Tests.SharePoint;

public sealed class SourceConnectorAuthorizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly Uri Callback = new("https://product.example/api/source-connections/callback");
    private static readonly Uri AdminCallback = new("https://product.example/api/source-connections/admin-consent-callback");
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private static readonly string[] ExpectedOAuthScopes =
    [
        "openid",
        "profile",
        "offline_access",
        "https://graph.microsoft.com/Files.Read.All",
        "https://graph.microsoft.com/Sites.Read.All",
    ];

    [Fact]
    public async Task Start_uses_organizations_authorization_code_pkce_s256_and_opaque_one_time_state()
    {
        var rig = new Rig();

        var started = await rig.Connector.BeginAsync(Request());

        var uri = new Uri(started.AuthorizationUrl);
        var query = QueryHelpers.ParseQuery(uri.Query);
        uri.GetLeftPart(UriPartial.Path).Should().Be(
            "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize");
        query["response_type"].Should().ContainSingle().Which.Should().Be("code");
        query["response_mode"].Should().ContainSingle().Which.Should().Be("query");
        query["redirect_uri"].Should().ContainSingle().Which.Should().Be(Callback.AbsoluteUri);
        query["code_challenge_method"].Should().ContainSingle().Which.Should().Be("S256");
        query["scope"].ToString().Split(' ').Should().Equal(ExpectedOAuthScopes);

        var state = query["state"].ToString();
        state.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        state.Should().NotContain("actor-product").And.NotContain("job-17").And.NotContain("sharepoint");
        rig.States.VisibleValues.Should().NotContain(state, "only a hash of callback state is retained");
        rig.States.Last!.StateHash.Should().Be(SourceAuthorizationCryptography.HashState(state));
        query["code_challenge"].ToString().Should().Be(
            SourceAuthorizationCryptography.CreateS256Challenge(rig.States.Last.CodeVerifier));
        rig.States.Last.ExpiresAt.Should().Be(Start.AddMinutes(10));
    }

    [Fact]
    public async Task Callback_state_is_redeemed_once_and_expired_or_replayed_state_never_redeems_a_code()
    {
        var rig = new Rig();
        var first = await rig.Connector.BeginAsync(Request());
        var state = Query(first.AuthorizationUrl, "state");

        (await rig.Processor.ProcessAsync(new SourceAuthorizationCallback("code-1", state)))
            .Kind.Should().Be(SourceCallbackOutcomeKind.Authorized);
        rig.Tokens.Calls.Should().Be(1);

        var replay = () => rig.Processor.ProcessAsync(new SourceAuthorizationCallback("code-1", state));
        (await replay.Should().ThrowAsync<SourceConnectorException>())
            .Which.Code.Should().Be("source_connection_invalid_or_expired");
        rig.Tokens.Calls.Should().Be(1);

        var expiring = await rig.Connector.BeginAsync(Request(connectionId: "connection-expired"));
        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        var expired = () => rig.Processor.ProcessAsync(
            new SourceAuthorizationCallback("code-secret", Query(expiring.AuthorizationUrl, "state")));
        (await expired.Should().ThrowAsync<SourceConnectorException>())
            .Which.Code.Should().Be("source_connection_invalid_or_expired");
        rig.Tokens.Calls.Should().Be(1);
    }

    [Fact]
    public async Task User_consent_completes_and_admin_consent_rotates_state_then_resumes_user_authorization()
    {
        var rig = new Rig();
        var user = await rig.Connector.BeginAsync(Request());

        var completed = await rig.Processor.ProcessAsync(
            new SourceAuthorizationCallback("user-consent-code", Query(user.AuthorizationUrl, "state")));
        completed.Kind.Should().Be(SourceCallbackOutcomeKind.Authorized);

        rig.Tokens.NextFailure = new SourceAdminConsentRequiredException(Tenant);
        var blocked = await rig.Connector.BeginAsync(Request(connectionId: "connection-admin"));
        var admin = await rig.Processor.ProcessAsync(
            new SourceAuthorizationCallback("admin-needed-code", Query(blocked.AuthorizationUrl, "state")));

        admin.Kind.Should().Be(SourceCallbackOutcomeKind.AdminConsentRequired);
        admin.SafeCode.Should().Be("source_admin_consent_required");
        var adminUri = new Uri(admin.AdminConsentUrl!);
        adminUri.AbsolutePath.Should().Be($"/{Tenant}/adminconsent");
        QueryHelpers.ParseQuery(adminUri.Query).Should().NotContainKey("scope");
        var adminState = Query(admin.AdminConsentUrl!, "state");
        adminState.Should().NotBe(Query(blocked.AuthorizationUrl, "state"));

        var resumed = await rig.Processor.ProcessAdminConsentAsync(
            new SourceAdminConsentCallback(adminState, Granted: true, ReturnedTenant: "attacker-tenant"));
        resumed.Kind.Should().Be(SourceCallbackOutcomeKind.AuthorizationRestarted);
        new Uri(resumed.AuthorizationUrl!).AbsolutePath.Should().StartWith("/organizations/");
        Query(resumed.AuthorizationUrl!, "state").Should().NotBe(adminState);
        rig.Tokens.Calls.Should().Be(2, "the untrusted tenant query value is never exchanged or accepted");
    }

    [Fact]
    public async Task Product_and_source_identities_remain_separate_and_only_a_source_binding_hash_leaves_the_processor()
    {
        var rig = new Rig();
        rig.Tokens.Result = ValidIdentity(accountId: "source.user@tenant.example");
        var started = await rig.Connector.BeginAsync(Request(productActorId: "product-user-42"));

        var result = await rig.Processor.ProcessAsync(
            new SourceAuthorizationCallback("code-1", Query(started.AuthorizationUrl, "state")));

        result.ProductActorId.Should().Be("product-user-42");
        result.SourceAccountBindingHash.Should().Be(
            SourceAuthorizationCryptography.HashAccount(Tenant, "source.user@tenant.example"));
        result.ToString().Should().NotContain("source.user@tenant.example");
        result.TokenCache.Should().Equal(9, 8, 7);
    }

    [Theory]
    [InlineData(false, "login.microsoftonline.com", Tenant, Tenant, "source_unsupported_account")]
    [InlineData(true, "login.microsoftonline.us", Tenant, Tenant, "source_unsupported_cloud")]
    [InlineData(true, "login.microsoftonline.com", Tenant, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "source_unsupported_account")]
    [InlineData(true, "login.microsoftonline.com", "9188040d-6c67-4c5b-b112-36a304b66dad", "9188040d-6c67-4c5b-b112-36a304b66dad", "source_unsupported_account")]
    public async Task Callback_accepts_only_consistent_organizational_commercial_cloud_accounts(
        bool organizational,
        string cloudHost,
        string tenantId,
        string accountTenantId,
        string expectedCode)
    {
        var rig = new Rig();
        rig.Tokens.Result = ValidIdentity(
            tenantId: tenantId,
            accountTenantId: accountTenantId,
            organizational: organizational,
            cloudHost: cloudHost);
        var started = await rig.Connector.BeginAsync(Request());

        var callback = () => rig.Processor.ProcessAsync(
            new SourceAuthorizationCallback("code-1", Query(started.AuthorizationUrl, "state")));

        (await callback.Should().ThrowAsync<SourceConnectorException>()).Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task Callback_errors_are_stable_and_do_not_echo_codes_state_accounts_or_provider_details()
    {
        var rig = new Rig();
        var started = await rig.Connector.BeginAsync(Request());
        var state = Query(started.AuthorizationUrl, "state");
        const string providerDetail = "AADSTS65001 secret.user@example.test";

        var callback = () => rig.Processor.ProcessAsync(
            new SourceAuthorizationCallback(null, state, "access_denied", providerDetail));

        var error = (await callback.Should().ThrowAsync<SourceConnectorException>()).Which;
        error.Code.Should().Be("source_access_denied");
        error.Message.Should().NotContain(providerDetail)
            .And.NotContain(state)
            .And.NotContain("AADSTS")
            .And.NotContain("example.test");
        rig.Tokens.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Managed_identity_assertion_is_injected_into_code_redemption_without_live_network()
    {
        var rig = new Rig();
        rig.Assertions.Value = "managed-identity-client-assertion";
        var started = await rig.Connector.BeginAsync(Request());

        await rig.Processor.ProcessAsync(
            new SourceAuthorizationCallback("authorization-code", Query(started.AuthorizationUrl, "state")));

        rig.Assertions.Calls.Should().Be(1);
        rig.Tokens.LastRequest!.ClientAssertion.Should().Be("managed-identity-client-assertion");
        rig.Tokens.LastRequest.AuthorizationCode.Should().Be("authorization-code");
        rig.Tokens.LastRequest.CodeVerifier.Should().Be(rig.States.Taken.Single().CodeVerifier);
        rig.Tokens.LastRequest.RedirectUri.Should().Be(Callback);
    }

    [Fact]
    public async Task Managed_identity_assertion_requests_only_the_Azure_AD_token_exchange_scope()
    {
        var credential = new RecordingCredential();
        var assertion = new ManagedIdentityClientAssertion(credential);

        (await assertion.GetClientAssertionAsync()).Should().Be("assertion-token");

        credential.Context!.Value.Scopes.Should().Equal("api://AzureADTokenExchange/.default");
    }

    private static SourceAuthorizationRequest Request(
        string connectionId = "connection-1",
        string productActorId = "actor-product") =>
        new(
            connectionId,
            productActorId,
            "job-17",
            "canonical-url-hash",
            "/jobs/job-17?sourceConnectionId=" + connectionId,
            TimeSpan.FromMinutes(10));

    private static SourceIdentityResult ValidIdentity(
        string tenantId = Tenant,
        string? accountTenantId = null,
        string accountId = "source-account-id",
        bool organizational = true,
        string cloudHost = "login.microsoftonline.com") =>
        new(
            tenantId,
            accountTenantId ?? tenantId,
            accountId,
            organizational,
            cloudHost,
            [9, 8, 7]);

    private static string Query(string url, string name) =>
        QueryHelpers.ParseQuery(new Uri(url).Query)[name].ToString();

    private sealed class Rig
    {
        public MutableTimeProvider Clock { get; } = new(Start);
        public MemoryStateStore States { get; } = new();
        public FakeTokenClient Tokens { get; } = new();
        public FakeAssertionProvider Assertions { get; } = new();
        public Microsoft365SourceConnector Connector { get; }
        public SourceCallbackProcessor Processor { get; }

        public Rig()
        {
            var options = new BulkScoringOptions
            {
                Mode = SharePointMode.Graph,
                SourceClientId = "connector-client-id",
                SourceAuthority = "https://login.microsoftonline.com/organizations",
                SourceCallbackPath = Callback.AbsolutePath,
                SourceAdminConsentCallbackPath = AdminCallback.AbsolutePath,
                SourceScopes = BulkScoringOptions.RequiredSourceScopes,
            };
            Connector = new Microsoft365SourceConnector(
                options,
                Callback,
                AdminCallback,
                States,
                Clock);
            Processor = new SourceCallbackProcessor(Connector, States, Tokens, Assertions, Clock);
        }
    }

    private sealed class MemoryStateStore : ISourceAuthorizationStateStore
    {
        private readonly ConcurrentDictionary<string, SourceAuthorizationState> _states = new();
        public SourceAuthorizationState? Last { get; private set; }
        public List<SourceAuthorizationState> Taken { get; } = [];
        public IEnumerable<string> VisibleValues => _states.Keys.Concat(
            _states.Values.SelectMany(value => new[]
            {
                value.ConnectionId,
                value.ProductActorId,
                value.JobId,
                value.CanonicalUrlHash,
                value.RedirectAfterPath,
                value.CodeVerifier,
            }));

        public Task StoreAsync(SourceAuthorizationState state, CancellationToken cancellationToken = default)
        {
            _states[state.StateHash] = state;
            Last = state;
            return Task.CompletedTask;
        }

        public Task<SourceAuthorizationState?> TakeAsync(
            string stateHash,
            CancellationToken cancellationToken = default)
        {
            _states.TryRemove(stateHash, out var state);
            if (state is not null) Taken.Add(state);
            return Task.FromResult(state);
        }
    }

    private sealed class FakeTokenClient : ISourceAuthorizationCodeClient
    {
        public int Calls { get; private set; }
        public SourceCodeRedemptionRequest? LastRequest { get; private set; }
        public SourceIdentityResult Result { get; set; } = ValidIdentity();
        public Exception? NextFailure { get; set; }

        public Task<SourceIdentityResult> RedeemAsync(
            SourceCodeRedemptionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            if (NextFailure is { } failure)
            {
                NextFailure = null;
                throw failure;
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeAssertionProvider : IClientAssertionProvider
    {
        public string Value { get; set; } = "fake-assertion";
        public int Calls { get; private set; }

        public ValueTask<string> GetClientAssertionAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(Value);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public TokenRequestContext? Context { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Context = requestContext;
            return new AccessToken("assertion-token", Start.AddMinutes(5));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            Context = requestContext;
            return ValueTask.FromResult(new AccessToken("assertion-token", Start.AddMinutes(5)));
        }
    }
}
