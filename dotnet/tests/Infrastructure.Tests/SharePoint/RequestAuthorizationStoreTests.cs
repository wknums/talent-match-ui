using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.Tests.SharePoint;

public sealed class RequestAuthorizationStoreTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string AlternateTenant = "33333333-3333-3333-3333-333333333333";
    private static readonly DateTime Start = DateTime.UtcNow;

    private static BulkScoringOptions Options(int maxAgeHours = 72) => new()
    {
        SourceClientId = "connector-client",
        SourceAuthority = "https://login.microsoftonline.com/organizations",
        SourceScopes = BulkScoringOptions.RequiredSourceScopes,
        AuthorizationMaxAge = TimeSpan.FromHours(maxAgeHours),
        ConnectionIdleTtl = TimeSpan.FromMinutes(30),
        ConnectionAbsoluteTtl = TimeSpan.FromHours(2),
    };

    private sealed class Rig
    {
        public FakeIdentityProvider Idp { get; } = new();
        public FakeKeys Keys { get; } = new();
        public MemoryAuthorizations Records { get; } = new();
        public TestClock Clock { get; } = new(Start);
        public BulkScoringOptions Options { get; init; } = RequestAuthorizationStoreTests.Options();

        public RequestAuthorizationStore Store() =>
            new(Records, Keys, Options, Clock, tenantId => Idp.NewClient(tenantId));

        public async Task<BulkRequestAuthorization> AddRequestAsync(
            string requestId,
            string actor = "actor-1",
            string drive = "drive-1",
            string folder = "folder-1",
            bool expiredAccessToken = false,
            string sourceTenant = Tenant)
        {
            var cache = await Idp.CreateAuthorizationCodeCacheAsync(expiredAccessToken, sourceTenant);
            var accountId = Idp.AccountIdentifier;
            var dto = new
            {
                ProductActorId = actor,
                SourceTenantId = sourceTenant,
                SourceAccountBindingHash = SourceAuthorizationCryptography.HashAccount(sourceTenant, accountId),
                SourceDriveId = drive,
                SourceFolderItemId = folder,
                Cache = Convert.ToBase64String(cache),
            };
            var plain = JsonSerializer.SerializeToUtf8Bytes(dto);
            var (payload, dataKey) = AuthorizationEnvelope.Seal(
                plain,
                AuthorizationEnvelope.RequestBinding(requestId));
            var wrapped = await Keys.WrapAsync(dataKey);
            AuthorizationEnvelope.Discard(plain, dataKey, cache);
            var record = BulkRequestAuthorization.Establish(
                requestId,
                actor,
                sourceTenant,
                dto.SourceAccountBindingHash,
                drive,
                folder,
                payload,
                wrapped.WrappedDataKey,
                wrapped.KeyVersion,
                Clock.GetUtcNow().UtcDateTime);
            await Records.SaveAsync(record);
            Idp.ResetCounters();
            return record;
        }
    }

    [Fact]
    public async Task Request_bound_authorization_code_cache_is_used_silently()
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync("request-1");

        var token = await rig.Store().AcquireGraphTokenAsync("request-1");

        token.Should().Be("authorization-access");
        rig.Idp.TokenRequests.Should().Be(0, "the unexpired delegated cache is sufficient");
        record.LastUsedAt.Should().Be(rig.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Expired_access_refreshes_and_persists_a_fresh_envelope_key()
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync("request-refresh", expiredAccessToken: true);
        var oldPayload = record.Payload!.ToArray();
        var oldWrappedKey = record.WrappedDataKey!.ToArray();

        var token = await rig.Store().AcquireGraphTokenAsync("request-refresh");

        token.Should().Be("refreshed-access-1");
        rig.Idp.RefreshRequests.Should().Be(1);
        record.Payload.Should().NotEqual(oldPayload);
        record.WrappedDataKey.Should().NotEqual(oldWrappedKey);
    }

    [Fact]
    public async Task Silent_renewal_uses_the_bound_tenant_authority()
    {
        var rig = new Rig();
        await rig.AddRequestAsync(
            "request-tenant",
            expiredAccessToken: true,
            sourceTenant: AlternateTenant);

        await rig.Store().AcquireGraphTokenAsync("request-tenant");

        rig.Idp.TokenTenants.Should().Equal(AlternateTenant);
    }

    public static TheoryData<string> BindingFields => new()
    {
        nameof(BulkRequestAuthorization.ProductActorId),
        nameof(BulkRequestAuthorization.SourceTenantId),
        nameof(BulkRequestAuthorization.SourceAccountBindingHash),
        nameof(BulkRequestAuthorization.SourceDriveId),
        nameof(BulkRequestAuthorization.SourceFolderItemId),
    };

    [Theory]
    [MemberData(nameof(BindingFields))]
    public async Task Every_bound_identity_and_source_dimension_is_enforced(string field)
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync($"request-binding-{field}");
        switch (field)
        {
            case nameof(BulkRequestAuthorization.ProductActorId):
                record.ProductActorId = "other-actor";
                break;
            case nameof(BulkRequestAuthorization.SourceTenantId):
                record.SourceTenantId = AlternateTenant;
                break;
            case nameof(BulkRequestAuthorization.SourceAccountBindingHash):
                record.SourceAccountBindingHash = new string('a', 64);
                break;
            case nameof(BulkRequestAuthorization.SourceDriveId):
                record.SourceDriveId = "other-drive";
                break;
            case nameof(BulkRequestAuthorization.SourceFolderItemId):
                record.SourceFolderItemId = "other-folder";
                break;
        }

        var act = () => rig.Store().AcquireGraphTokenAsync(record.RequestId);

        (await act.Should().ThrowAsync<SourceAuthorizationLapsedException>())
            .Which.Reason.Should().Be("authorization_unreadable");
        rig.Idp.TokenRequests.Should().Be(0, "no application, product, or other-user fallback is allowed");
    }

    [Fact]
    public async Task Every_request_actor_tenant_account_drive_and_folder_binding_is_checked()
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync("request-bound");
        record.SourceFolderItemId = "stolen-folder";

        var act = () => rig.Store().AcquireGraphTokenAsync("request-bound");

        (await act.Should().ThrowAsync<SourceAuthorizationLapsedException>())
            .Which.Reason.Should().Be("authorization_unreadable");
        rig.Idp.TokenRequests.Should().Be(0);
    }

    [Fact]
    public async Task Process_token_reuse_never_bypasses_a_changed_persisted_binding()
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync("request-reuse-binding");
        var store = rig.Store();
        await store.AcquireGraphTokenAsync("request-reuse-binding");
        record.SourceDriveId = "different-drive";

        var act = () => store.AcquireGraphTokenAsync("request-reuse-binding");

        (await act.Should().ThrowAsync<SourceAuthorizationLapsedException>())
            .Which.Reason.Should().Be("authorization_unreadable");
    }

    [Fact]
    public async Task Cache_cannot_be_transferred_by_copying_ciphertext_to_another_request()
    {
        var rig = new Rig();
        var source = await rig.AddRequestAsync("request-source");
        var stolen = BulkRequestAuthorization.Establish(
            "request-target",
            source.ProductActorId,
            source.SourceTenantId,
            source.SourceAccountBindingHash,
            source.SourceDriveId,
            source.SourceFolderItemId,
            source.Payload!.ToArray(),
            source.WrappedDataKey!.ToArray(),
            source.KeyVersion,
            source.ObtainedAt);
        await rig.Records.SaveAsync(stolen);

        var act = () => rig.Store().AcquireGraphTokenAsync("request-target");

        (await act.Should().ThrowAsync<SourceAuthorizationLapsedException>())
            .Which.Reason.Should().Be("authorization_unreadable");
        rig.Idp.TokenRequests.Should().Be(0);
    }

    [Fact]
    public async Task Key_rotation_rewraps_an_unchanged_cache_with_a_fresh_data_key()
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync("request-rotate");
        var oldPayload = record.Payload!.ToArray();
        rig.Keys.Rotate();

        await rig.Store().AcquireGraphTokenAsync("request-rotate");

        record.KeyVersion.Should().Be(rig.Keys.CurrentKeyVersion);
        record.Payload.Should().NotEqual(oldPayload);
    }

    [Fact]
    public async Task Absolute_age_lapse_and_discard_clear_material_without_fallback()
    {
        var rig = new Rig { Options = Options(maxAgeHours: 1) };
        var old = await rig.AddRequestAsync("request-old");
        var discarded = await rig.AddRequestAsync("request-discard");
        rig.Clock.Advance(TimeSpan.FromHours(2));

        var oldAct = () => rig.Store().AcquireGraphTokenAsync("request-old");
        (await oldAct.Should().ThrowAsync<SourceAuthorizationLapsedException>())
            .Which.Reason.Should().Be("max_age_exceeded");
        await rig.Store().DiscardAsync("request-discard");

        old.IsActive.Should().BeTrue("the worker records the lapse in its transaction boundary");
        discarded.Status.Should().Be(BulkAuthorizationStatus.Discarded);
        discarded.Payload.Should().BeNull();
        discarded.WrappedDataKey.Should().BeNull();
        rig.Idp.TokenRequests.Should().Be(0);
    }

    [Fact]
    public async Task Permanent_refresh_refusal_lapses_without_obo_or_application_fallback()
    {
        var rig = new Rig();
        await rig.AddRequestAsync("request-refused", expiredAccessToken: true);
        rig.Idp.NextRefresh = FakeIdentityProvider.Refuse("AADSTS50173");

        var act = () => rig.Store().AcquireGraphTokenAsync("request-refused");

        (await act.Should().ThrowAsync<SourceAuthorizationLapsedException>())
            .Which.Reason.Should().Be("session_revoked");
        rig.Idp.RefreshRequests.Should().Be(1);
        rig.Idp.AuthorizationCodeRequests.Should().Be(0);
    }

    [Theory]
    [InlineData("session_expired", BulkAuthorizationStatus.Lapsed)]
    [InlineData("session_revoked", BulkAuthorizationStatus.Revoked)]
    [InlineData("account_disabled", BulkAuthorizationStatus.AccessLost)]
    [InlineData("conditional_access", BulkAuthorizationStatus.AccessLost)]
    public async Task Background_lapse_classifies_lapse_revoke_and_access_loss(
        string reason,
        BulkAuthorizationStatus expected)
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync($"request-{reason}");

        await rig.Store().MarkLapsedAsync(record.RequestId, reason);

        record.Status.Should().Be(expected);
        record.Payload.Should().BeNull();
        record.WrappedDataKey.Should().BeNull();
    }

    [Fact]
    public async Task Lapsed_authorization_can_be_discarded_without_restoring_ciphertext()
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync("request-lapsed-discard");
        await rig.Store().MarkLapsedAsync(record.RequestId, "session_expired");

        await rig.Store().DiscardAsync(record.RequestId);

        record.Status.Should().Be(BulkAuthorizationStatus.Discarded);
        record.Payload.Should().BeNull();
        record.WrappedDataKey.Should().BeNull();
    }

    [Fact]
    public async Task Missing_key_is_a_lapse_but_key_service_outage_is_transient()
    {
        var rig = new Rig();
        var record = await rig.AddRequestAsync("request-key");
        rig.Keys.Outage = true;

        await FluentActions.Awaiting(() => rig.Store().AcquireGraphTokenAsync("request-key"))
            .Should().ThrowAsync<SourceTransientException>();
        record.IsActive.Should().BeTrue();

        rig.Keys.Outage = false;
        rig.Keys.Forget(record.KeyVersion);
        (await FluentActions.Awaiting(() => rig.Store().AcquireGraphTokenAsync("request-key"))
                .Should().ThrowAsync<SourceAuthorizationLapsedException>())
            .Which.Reason.Should().Be("key_unavailable");
    }

    [Fact]
    public async Task Authorization_audits_use_required_actors_safe_facts_and_survive_ciphertext_cleanup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

        var establishment = BulkAuditWriter.User(
            BulkAuditEventTypes.RequestAuthorizationEstablished,
            "authenticated-user",
            Guid.NewGuid().ToString(),
            Start,
            "request_authorization",
            "audit-established",
            new { state = BulkAuthorizationStatus.Active.ToWire() });
        context.BulkAuditEvents.Add(establishment);

        var used = Authorization("audit-used");
        var lapsed = Authorization("audit-lapsed");
        var discarded = Authorization("audit-discarded");
        context.BulkRequestAuthorizations.AddRange(used, lapsed, discarded);
        await context.SaveChangesAsync();

        var repository = new BulkRequestAuthorizationRepository(context);
        used.RecordUse(Start.AddMinutes(1));
        await repository.SaveAsync(used);
        lapsed.MarkLapsed("session_expired", Start.AddMinutes(2));
        await repository.SaveAsync(lapsed);
        discarded.Discard(Start.AddMinutes(3));
        await repository.SaveAsync(discarded);

        var events = await context.BulkAuditEvents
            .OrderBy(row => row.OccurredAt)
            .ToListAsync();
        events.Select(row => row.EventType).Should().BeEquivalentTo(
            [
                BulkAuditEventTypes.RequestAuthorizationEstablished,
                BulkAuditEventTypes.RequestAuthorizationUsed,
                BulkAuditEventTypes.RequestAuthorizationLapsed,
                BulkAuditEventTypes.RequestAuthorizationDiscarded,
            ]);
        events.Single(row => row.EventType == BulkAuditEventTypes.RequestAuthorizationEstablished)
            .Should().Match<BulkAuditEvent>(row =>
                row.ActorType == BulkAuditActorType.User
                && row.ActorId == "authenticated-user"
                && row.InitiatedByActorId == null);
        events.Single(row => row.EventType == BulkAuditEventTypes.RequestAuthorizationUsed)
            .ActorId.Should().Be("bulk-scoring-worker");
        events.Single(row => row.EventType == BulkAuditEventTypes.RequestAuthorizationLapsed)
            .ActorId.Should().Be("bulk-scoring-worker");
        events.Single(row => row.EventType == BulkAuditEventTypes.RequestAuthorizationDiscarded)
            .ActorId.Should().Be("bulk-authorization-timer");
        events.Where(row => row.ActorType == BulkAuditActorType.Service)
            .Should().OnlyContain(row => row.InitiatedByActorId == "actor-1");
        events.All(HasValidAuditEnvelope).Should().BeTrue();
        events.Should().OnlyContain(row =>
            !row.SafeFactsJson.Contains("actor-1", StringComparison.Ordinal)
            && !row.SafeFactsJson.Contains("drive-1", StringComparison.Ordinal)
            && !row.SafeFactsJson.Contains("folder-1", StringComparison.Ordinal)
            && !row.SafeFactsJson.Contains("authorization-access", StringComparison.Ordinal));

        context.BulkRequestAuthorizations.RemoveRange(used, lapsed, discarded);
        await context.SaveChangesAsync();
        context.BulkAuditEvents.Should().HaveCount(4, "detached audit survives ciphertext cleanup");

        var immutable = events[0];
        context.Entry(immutable).Property(row => row.EventType).CurrentValue =
            BulkAuditEventTypes.RequestAuthorizationDiscarded;
        await FluentActions.Awaiting(() => context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*append-only*");
        context.Entry(immutable).State = EntityState.Unchanged;
        context.BulkAuditEvents.Remove(immutable);
        await FluentActions.Awaiting(() => context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public void SQLite_and_SQL_Server_models_keep_identical_authorization_audit_facts()
    {
        using var sqlite = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var sqlServer = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=RequestAuthorizationModelOnly;Trusted_Connection=True")
                .Options);

        var sqliteAuthorization = sqlite.Model.FindEntityType(typeof(BulkRequestAuthorization))!;
        var sqlAuthorization = sqlServer.Model.FindEntityType(typeof(BulkRequestAuthorization))!;
        sqliteAuthorization.GetProperties().Select(property => property.Name).Should()
            .Equal(sqlAuthorization.GetProperties().Select(property => property.Name));

        var sqliteAudit = sqlite.Model.FindEntityType(typeof(BulkAuditEvent))!;
        var sqlAudit = sqlServer.Model.FindEntityType(typeof(BulkAuditEvent))!;
        sqliteAudit.GetProperties().Select(property => property.Name).Should()
            .Equal(sqlAudit.GetProperties().Select(property => property.Name));
        sqliteAudit.GetForeignKeys().Should().BeEmpty();
        sqlAudit.GetForeignKeys().Should().BeEmpty();
    }

    private static BulkRequestAuthorization Authorization(string requestId) =>
        BulkRequestAuthorization.Establish(
            requestId,
            "actor-1",
            Tenant,
            new string('b', 64),
            "drive-1",
            "folder-1",
            [1, 2, 3],
            [4, 5, 6],
            "key/v1",
            Start);

    private static bool HasValidAuditEnvelope(BulkAuditEvent row) =>
        Guid.TryParse(row.CorrelationId, out _)
        && row.OccurredAt.Kind == DateTimeKind.Utc
        && row.SchemaVersion == 1
        && JsonDocument.Parse(row.SafeFactsJson).RootElement.GetProperty("factVersion").GetInt32() == 1;

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        private DateTime _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }

    private sealed class MemoryAuthorizations : IBulkRequestAuthorizationRepository
    {
        private readonly Dictionary<string, BulkRequestAuthorization> _rows = [];

        public Task<BulkRequestAuthorization?> GetAsync(
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.GetValueOrDefault(requestId));

        public Task SaveAsync(
            BulkRequestAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            _rows[authorization.RequestId] = authorization;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListActiveObtainedBeforeAsync(
            DateTime cutoffUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListDiscardableAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeKeys : IAuthorizationKeyWrapper
    {
        private readonly Dictionary<string, byte[]> _versions = new()
        {
            ["key/v1"] = RandomNumberGenerator.GetBytes(32),
        };
        private string _current = "key/v1";

        public bool Outage { get; set; }
        public string CurrentKeyVersion => _current;

        public void Rotate()
        {
            _current = $"key/v{_versions.Count + 1}";
            _versions[_current] = RandomNumberGenerator.GetBytes(32);
        }

        public void Forget(string version) => _versions.Remove(version);

        public Task<AuthorizationSecret> WrapAsync(
            byte[] dataKey,
            CancellationToken cancellationToken = default)
        {
            if (Outage)
                throw new RequestFailedException(503, "vault unavailable");
            var key = _versions[_current];
            return Task.FromResult(new AuthorizationSecret(
                dataKey.Select((value, index) => (byte)(value ^ key[index])).ToArray(),
                _current));
        }

        public Task<byte[]> UnwrapAsync(
            byte[] wrappedDataKey,
            string keyVersion,
            CancellationToken cancellationToken = default)
        {
            if (Outage)
                throw new RequestFailedException(503, "vault unavailable");
            if (!_versions.TryGetValue(keyVersion, out var key))
                throw new RequestFailedException(404, "key unavailable");
            return Task.FromResult(
                wrappedDataKey.Select((value, index) => (byte)(value ^ key[index])).ToArray());
        }
    }

    private sealed class FakeIdentityProvider : HttpMessageHandler
    {
        private int _refreshRequests;
        private int _authorizationCodeRequests;
        private int _tokenRequests;
        private readonly List<string> _tokenTenants = [];

        public int RefreshRequests => _refreshRequests;
        public int AuthorizationCodeRequests => _authorizationCodeRequests;
        public int TokenRequests => _tokenRequests;
        public IReadOnlyList<string> TokenTenants => _tokenTenants;
        public string AccountIdentifier { get; private set; } = string.Empty;
        public Func<HttpResponseMessage>? NextRefresh { get; set; }

        public void ResetCounters()
        {
            _refreshRequests = 0;
            _authorizationCodeRequests = 0;
            _tokenRequests = 0;
            _tokenTenants.Clear();
        }

        public static Func<HttpResponseMessage> Refuse(string aadsts) =>
            () => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    error = "invalid_grant",
                    error_description = $"{aadsts}: The refresh token is not valid.",
                    error_codes = new[] { int.Parse(aadsts.Replace("AADSTS", "")) },
                }), Encoding.UTF8, "application/json"),
            };

        public IConfidentialClientApplication NewClient(string tenantId = Tenant) =>
            ConfidentialClientApplicationBuilder.Create("connector-client")
                .WithClientSecret("test-secret")
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}", validateAuthority: false)
                .WithRedirectUri("https://product.example.test/callback")
                .WithHttpClientFactory(new Factory(this))
                .Build();

        public async Task<byte[]> CreateAuthorizationCodeCacheAsync(bool expired, string tenantId = Tenant)
        {
            var app = NewClient(tenantId);
            byte[] cache = [];
            app.UserTokenCache.SetAfterAccess(args =>
            {
                if (args.HasStateChanged)
                    cache = args.TokenCache.SerializeMsalV3();
            });
            await app.AcquireTokenByAuthorizationCode(
                    [
                        "https://graph.microsoft.com/Files.Read.All",
                        "https://graph.microsoft.com/Sites.Read.All",
                    ],
                    "authorization-code")
                .ExecuteAsync();
            AccountIdentifier = (await app.GetAccountsAsync()).Single().HomeAccountId.Identifier;
            if (expired)
            {
                var root = JsonNode.Parse(cache)!.AsObject();
                foreach (var token in root["AccessToken"]!.AsObject().Select(entry => entry.Value!.AsObject()))
                    token["expires_on"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
                cache = Encoding.UTF8.GetBytes(root.ToJsonString());
            }
            return cache;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                var discoveryTenant = TenantFromPath(path);
                return Json(new
                {
                    token_endpoint = $"https://login.microsoftonline.com/{discoveryTenant}/oauth2/v2.0/token",
                    authorization_endpoint = $"https://login.microsoftonline.com/{discoveryTenant}/oauth2/v2.0/authorize",
                    issuer = $"https://login.microsoftonline.com/{discoveryTenant}/v2.0",
                });
            }
            if (path.Contains("/discovery/instance", StringComparison.Ordinal))
                return Json(new
                {
                    tenant_discovery_endpoint =
                        $"https://login.microsoftonline.com/{Tenant}/v2.0/.well-known/openid-configuration",
                    metadata = Array.Empty<object>(),
                });

            Interlocked.Increment(ref _tokenRequests);
            var tokenTenant = TenantFromPath(path);
            _tokenTenants.Add(tokenTenant);
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
                .Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(
                    part => Uri.UnescapeDataString(part[0]),
                    part => Uri.UnescapeDataString(
                        part.Length > 1 ? part[1].Replace('+', ' ') : ""));
            if (form.GetValueOrDefault("grant_type") == "refresh_token")
            {
                Interlocked.Increment(ref _refreshRequests);
                if (NextRefresh is { } refusal)
                    return refusal();
                return Tokens("refreshed-access-" + _refreshRequests, "refresh-rotated", tokenTenant);
            }

            Interlocked.Increment(ref _authorizationCodeRequests);
            return Tokens("authorization-access", "authorization-refresh", tokenTenant);
        }

        private static string TenantFromPath(string path) =>
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Tenant;

        private static HttpResponseMessage Tokens(
            string accessToken,
            string refreshToken,
            string tenantId) =>
            Json(new
            {
                token_type = "Bearer",
                scope = "https://graph.microsoft.com/Files.Read.All https://graph.microsoft.com/Sites.Read.All",
                expires_in = 3600,
                ext_expires_in = 7200,
                access_token = accessToken,
                refresh_token = refreshToken,
                client_info = Encode(new { uid = "user-1", utid = tenantId }),
                id_token = $"{Encode(new { alg = "none", typ = "JWT" })}.{Encode(new
                {
                    aud = "connector-client",
                    iss = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                    iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                    oid = "22222222-2222-2222-2222-222222222222",
                    tid = tenantId,
                    sub = "subject-1",
                    preferred_username = "recruiter@contoso.test",
                    name = "Recruiter",
                })}.",
            });

        private static string Encode(object value) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json"),
            };

        private sealed class Factory(HttpMessageHandler handler) : IMsalHttpClientFactory
        {
            public HttpClient GetHttpClient() => new(handler, disposeHandler: false);
        }
    }
}
