using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.Tests.SharePoint;

public sealed class PendingSourceAuthorizationStoreTests
{
    private static readonly DateTime Start = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);
    private static readonly PendingSourceAuthorizationBinding Binding =
        new("connection-1", "actor-1", "job-1", "folder-hash-1");

    private sealed class Rig
    {
        public MemoryConnections Connections { get; } = new();
        public RotatingKeys Keys { get; } = new();
        public TestClock Clock { get; } = new(Start);
        public BulkScoringOptions Options { get; } = new()
        {
            ConnectionIdleTtl = TimeSpan.FromMinutes(30),
            ConnectionAbsoluteTtl = TimeSpan.FromHours(2),
        };

        public PendingSourceAuthorizationStore Store() => new(Connections, Keys, Options, Clock);

        public async Task<BulkSourceConnection> AuthorizedAsync(
            PendingSourceAuthorizationBinding? binding = null,
            string stateHash = "state-1")
        {
            var actual = binding ?? Binding;
            var connection = BulkSourceConnection.Begin(
                actual.ConnectionId,
                actual.ProductActorId,
                actual.JobId,
                actual.CanonicalUrlHash,
                Encoding.UTF8.GetBytes("encrypted-folder-placeholder"),
                stateHash,
                Encoding.UTF8.GetBytes("encrypted-verifier-placeholder"),
                "/jobs/job-1/source",
                Clock.GetUtcNow().UtcDateTime,
                Options.ConnectionIdleTtl,
                Options.ConnectionAbsoluteTtl);
            await Connections.AddAsync(connection);
            await Store().StoreAuthorizedCacheAsync(
                actual,
                stateHash,
                "tenant-1",
                "account-hash-1",
                Encoding.UTF8.GetBytes("msal-cache-secret"));
            return connection;
        }
    }

    [Fact]
    public async Task Pending_cache_is_encrypted_and_bound_to_connection_actor_job_and_folder()
    {
        var rig = new Rig();
        var connection = await rig.AuthorizedAsync();

        var visible = Encoding.UTF8.GetString(connection.ProtectedPayload!)
            + Convert.ToBase64String(connection.ProtectedPayload!);
        visible.Should().NotContain("msal-cache-secret");
        connection.WrappedDataKey.Should().NotBeNullOrEmpty();

        var opened = await rig.Store().OpenAsync(Binding);
        Encoding.UTF8.GetString(opened.TokenCache).Should().Be("msal-cache-secret");

        foreach (var wrong in new[]
        {
            Binding with { ConnectionId = "connection-2" },
            Binding with { ProductActorId = "actor-2" },
            Binding with { JobId = "job-2" },
            Binding with { CanonicalUrlHash = "folder-hash-2" },
        })
        {
            await FluentActions.Awaiting(() => rig.Store().OpenAsync(wrong))
                .Should().ThrowAsync<PendingSourceAuthorizationException>()
                .Where(error => error.Code == "source_connection_invalid_or_expired");
        }
    }

    [Fact]
    public async Task Idle_expiry_clears_the_pending_cache_without_opening_it()
    {
        var rig = new Rig();
        var connection = await rig.AuthorizedAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(30));

        await FluentActions.Awaiting(() => rig.Store().OpenAsync(Binding))
            .Should().ThrowAsync<PendingSourceAuthorizationException>()
            .Where(error => error.Code == "source_connection_invalid_or_expired");

        connection.Status.Should().Be(BulkSourceConnectionStatus.Expired);
        connection.ProtectedPayload.Should().BeNull();
        connection.WrappedDataKey.Should().BeNull();
    }

    [Fact]
    public async Task Touch_extends_idle_expiry_but_never_the_absolute_expiry()
    {
        var rig = new Rig();
        var connection = await rig.AuthorizedAsync();

        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        await rig.Store().TouchAsync(Binding);
        connection.ExpiresAt.Should().Be(Start.AddMinutes(50));

        for (var step = 0; step < 4; step++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(20));
            await rig.Store().TouchAsync(Binding);
        }
        connection.ExpiresAt.Should().Be(Start.AddHours(2));

        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        await FluentActions.Awaiting(() => rig.Store().TouchAsync(Binding))
            .Should().ThrowAsync<PendingSourceAuthorizationException>();
        connection.Status.Should().Be(BulkSourceConnectionStatus.Expired);
    }

    [Fact]
    public async Task Reading_after_key_rotation_rewraps_with_a_fresh_envelope_key()
    {
        var rig = new Rig();
        var connection = await rig.AuthorizedAsync();
        var oldPayload = connection.ProtectedPayload!.ToArray();
        var oldWrappedKey = connection.WrappedDataKey!.ToArray();
        rig.Keys.Rotate();

        var opened = await rig.Store().OpenAsync(Binding);

        Encoding.UTF8.GetString(opened.TokenCache).Should().Be("msal-cache-secret");
        connection.KeyVersion.Should().Be(rig.Keys.CurrentKeyVersion);
        connection.ProtectedPayload.Should().NotEqual(oldPayload);
        connection.WrappedDataKey.Should().NotEqual(oldWrappedKey);
    }

    [Fact]
    public async Task Abandon_clears_every_pending_secret_and_cannot_be_reopened()
    {
        var rig = new Rig();
        var connection = await rig.AuthorizedAsync();

        await rig.Store().AbandonAsync(Binding);

        connection.Status.Should().Be(BulkSourceConnectionStatus.Abandoned);
        connection.PendingFolderUrlEncrypted.Should().BeNull();
        connection.PkceVerifierEncrypted.Should().BeNull();
        connection.ProtectedPayload.Should().BeNull();
        connection.WrappedDataKey.Should().BeNull();
        await FluentActions.Awaiting(() => rig.Store().OpenAsync(Binding))
            .Should().ThrowAsync<PendingSourceAuthorizationException>();
    }

    [Fact]
    public async Task Callback_failure_clears_authorization_code_state_verifier_and_cache()
    {
        var rig = new Rig();
        var connection = await rig.AuthorizedAsync();

        await rig.Store().ClearCallbackFailureAsync(Binding, "source_access_denied");

        connection.Status.Should().Be(BulkSourceConnectionStatus.Failed);
        connection.FailureCode.Should().Be("source_access_denied");
        connection.StateHash.Should().BeNull();
        connection.PkceVerifierEncrypted.Should().BeNull();
        connection.ProtectedPayload.Should().BeNull();
        connection.WrappedDataKey.Should().BeNull();
    }

    [Fact]
    public async Task Transfer_reencrypts_for_the_request_and_consumes_the_pending_connection()
    {
        var rig = new Rig();
        var connection = await rig.AuthorizedAsync();
        connection.VerifyFolder(
            BulkSourceKind.SharePoint,
            "drive-1",
            "folder-1",
            "Candidates",
            "/Candidates",
            "https://contoso.sharepoint.com/Candidates",
            null,
            rig.Clock.GetUtcNow().UtcDateTime,
            rig.Options.ConnectionIdleTtl,
            rig.Options.ConnectionAbsoluteTtl);
        await rig.Connections.SaveAsync(connection);
        var pendingPayload = connection.ProtectedPayload!.ToArray();

        var request = await rig.Store().TransferToRequestAsync(Binding, "request-1");

        request.IsBoundTo(
            "request-1",
            Binding.ProductActorId,
            "tenant-1",
            "account-hash-1",
            "drive-1",
            "folder-1").Should().BeTrue();
        connection.Status.Should().Be(BulkSourceConnectionStatus.Consumed);
        connection.ProtectedPayload.Should().BeNull();
        request.Payload.Should().NotEqual(pendingPayload);

        var key = await rig.Keys.UnwrapAsync(request.WrappedDataKey!, request.KeyVersion);
        AuthorizationEnvelope.Open(request.Payload!, key, AuthorizationEnvelope.RequestBinding("request-1"))
            .Should().NotBeEmpty();
        FluentActions.Invoking(() => AuthorizationEnvelope.Open(
                request.Payload!,
                key,
                AuthorizationEnvelope.PendingBinding(
                    Binding.ConnectionId,
                    Binding.ProductActorId,
                    Binding.JobId,
                    Binding.CanonicalUrlHash)))
            .Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task Transfer_rejects_an_unverified_folder_and_cannot_cross_a_binding()
    {
        var rig = new Rig();
        await rig.AuthorizedAsync();

        await FluentActions.Awaiting(() => rig.Store().TransferToRequestAsync(Binding, "request-1"))
            .Should().ThrowAsync<PendingSourceAuthorizationException>()
            .Where(error => error.Code == "source_connection_not_verified");

        await FluentActions.Awaiting(() => rig.Store().TransferToRequestAsync(
                Binding with { ProductActorId = "actor-2" },
                "request-1"))
            .Should().ThrowAsync<PendingSourceAuthorizationException>();
    }

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        private DateTime _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }

    private sealed class MemoryConnections : IBulkSourceConnectionRepository
    {
        private readonly Dictionary<string, BulkSourceConnection> _connections = [];
        private readonly Dictionary<string, BulkRequestAuthorization> _requests = [];

        public Task AddAsync(BulkSourceConnection connection, CancellationToken cancellationToken = default)
        {
            _connections.Add(connection.Id, connection);
            return Task.CompletedTask;
        }

        public Task<BulkSourceConnection?> GetBoundAsync(
            string connectionId,
            string productActorId,
            string jobId,
            string canonicalUrlHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_connections.GetValueOrDefault(connectionId) is { } connection
                && connection.IsBoundTo(connectionId, productActorId, jobId, canonicalUrlHash)
                    ? connection
                    : null);

        public Task SaveAsync(BulkSourceConnection connection, CancellationToken cancellationToken = default)
        {
            _connections[connection.Id] = connection;
            return Task.CompletedTask;
        }

        public Task<bool> ExpireAsync(string connectionId, DateTime utcNow, CancellationToken cancellationToken = default)
        {
            if (!_connections.TryGetValue(connectionId, out var connection) || !connection.IsPending)
                return Task.FromResult(false);
            connection.Expire(utcNow);
            return Task.FromResult(true);
        }

        public Task<bool> AbandonAsync(
            string connectionId,
            string productActorId,
            string jobId,
            DateTime utcNow,
            CancellationToken cancellationToken = default)
        {
            if (!_connections.TryGetValue(connectionId, out var connection)
                || connection.ProductActorId != productActorId
                || connection.JobId != jobId
                || !connection.IsPending)
                return Task.FromResult(false);
            connection.Abandon(utcNow);
            return Task.FromResult(true);
        }

        public Task<BulkRequestAuthorization> TransferToRequestAsync(
            string connectionId,
            string productActorId,
            string jobId,
            string canonicalUrlHash,
            string requestId,
            byte[] protectedPayload,
            byte[] wrappedDataKey,
            string keyVersion,
            DateTime utcNow,
            CancellationToken cancellationToken = default)
        {
            var connection = _connections[connectionId];
            if (!connection.IsBoundTo(connectionId, productActorId, jobId, canonicalUrlHash)
                || connection.Status != BulkSourceConnectionStatus.Verified)
                throw new InvalidOperationException("source_connection_not_verified");
            if (_requests.ContainsKey(requestId))
                throw new InvalidOperationException("request_authorization_already_exists");
            var authorization = BulkRequestAuthorization.Establish(
                requestId,
                productActorId,
                connection.SourceTenantId!,
                connection.SourceAccountBindingHash!,
                connection.SourceDriveId!,
                connection.SourceFolderItemId!,
                protectedPayload,
                wrappedDataKey,
                keyVersion,
                utcNow);
            _requests.Add(requestId, authorization);
            connection.Consume(requestId, utcNow);
            return Task.FromResult(authorization);
        }
    }

    private sealed class RotatingKeys : IAuthorizationKeyWrapper
    {
        private readonly Dictionary<string, byte[]> _keys = new()
        {
            ["key/v1"] = RandomNumberGenerator.GetBytes(32),
        };
        private string _current = "key/v1";

        public string CurrentKeyVersion => _current;

        public void Rotate()
        {
            _current = $"key/v{_keys.Count + 1}";
            _keys.Add(_current, RandomNumberGenerator.GetBytes(32));
        }

        public Task<AuthorizationSecret> WrapAsync(byte[] dataKey, CancellationToken cancellationToken = default)
        {
            var mask = _keys[_current];
            return Task.FromResult(new AuthorizationSecret(
                dataKey.Select((value, index) => (byte)(value ^ mask[index])).ToArray(),
                _current));
        }

        public Task<byte[]> UnwrapAsync(
            byte[] wrappedDataKey,
            string keyVersion,
            CancellationToken cancellationToken = default)
        {
            var mask = _keys[keyVersion];
            return Task.FromResult(wrappedDataKey.Select((value, index) => (byte)(value ^ mask[index])).ToArray());
        }
    }
}
