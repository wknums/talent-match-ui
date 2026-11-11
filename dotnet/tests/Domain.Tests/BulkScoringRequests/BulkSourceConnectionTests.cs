using FluentAssertions;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Domain.Tests.BulkScoringRequests;

public sealed class BulkSourceConnectionTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Absolute = TimeSpan.FromHours(2);

    [Fact]
    public void Pending_connection_domain_contract_has_complete_binding_and_lifecycle_surface()
    {
        var type = typeof(BulkRequestAuthorization).Assembly.GetType(
            "TalentMatch.Domain.Entities.BulkScoringRequests.BulkSourceConnection");

        type.Should().NotBeNull("pending delegated authorization must be a first-class domain entity");
        string[] properties =
        [
            "Id", "ProductActorId", "JobId", "CanonicalUrlHash", "StateHash",
            "Status", "ObtainedAt", "LastActivityAt", "ExpiresAt", "ConsumedByRequestId",
            "PendingFolderUrlEncrypted", "PkceVerifierEncrypted", "ProtectedPayload",
            "WrappedDataKey", "KeyVersion", "ConcurrencyVersion",
        ];
        properties.Should().AllSatisfy(name => type!.GetProperty(name).Should().NotBeNull());
        string[] methods = ["IsBoundTo", "IsExpired", "CompleteAuthorization", "VerifyFolder", "Touch", "Consume", "Abandon", "Expire", "Fail"];
        methods.Should().AllSatisfy(name => type!.GetMethods().Select(method => method.Name).Should().Contain(name));
    }

    [Fact]
    public void Connection_state_registry_is_one_time_and_has_no_reusable_account_state()
    {
        var enumType = typeof(BulkRequestAuthorization).Assembly.GetType(
            "TalentMatch.Domain.Entities.BulkScoringRequests.BulkSourceConnectionStatus");
        enumType.Should().NotBeNull();
        Enum.GetNames(enumType!).Select(ToSnakeCase).Should().Equal(
            "authorizing", "authorized", "verified", "consumed", "expired", "abandoned", "failed");
    }

    [Fact]
    public void Binding_expiry_one_time_state_consume_and_cryptographic_clearing_are_enforced()
    {
        var pendingUrl = new byte[] { 1, 2, 3 };
        var verifier = new byte[] { 4, 5, 6 };
        var connection = BulkSourceConnection.Begin(
            "conn-1", "actor-1", "job-1", "url-hash", pendingUrl, "state-hash", verifier,
            "/jobs/job-1?sourceConnectionId=conn-1", Now, Idle, Absolute);

        connection.IsBoundTo("conn-1", "actor-1", "job-1", "url-hash").Should().BeTrue();
        connection.IsBoundTo("conn-1", "actor-2", "job-1", "url-hash").Should().BeFalse();
        connection.ExpiresAt.Should().Be(Now + Idle);
        connection.IsExpired(Now + Idle).Should().BeTrue();

        connection.Touch(Now + TimeSpan.FromMinutes(20), Idle, Absolute);
        connection.ExpiresAt.Should().Be(Now + TimeSpan.FromMinutes(50));

        var cache = new byte[] { 7, 8, 9 };
        var key = new byte[] { 10, 11, 12 };
        connection.CompleteAuthorization(
            "state-hash", "tenant-1", "account-hash", cache, key, "key-v1",
            Now + TimeSpan.FromMinutes(21), Idle, Absolute);
        verifier.Should().OnlyContain(value => value == 0);
        connection.StateHash.Should().BeNull();
        var replay = () => connection.CompleteAuthorization(
            "state-hash", "tenant-1", "account-hash", [1], [2], "key-v1",
            Now + TimeSpan.FromMinutes(22), Idle, Absolute);
        replay.Should().Throw<InvalidOperationException>();

        connection.VerifyFolder(
            BulkSourceKind.SharePoint, "drive-1", "folder-1", "Candidates", "/Candidates",
            "https://tenant-a.sharepoint.com/sites/hr/Candidates", null,
            Now + TimeSpan.FromMinutes(22), Idle, Absolute);
        connection.Consume("request-1", Now + TimeSpan.FromMinutes(23));

        connection.Status.Should().Be(BulkSourceConnectionStatus.Consumed);
        connection.ConsumedByRequestId.Should().Be("request-1");
        connection.ProtectedPayload.Should().BeNull();
        connection.WrappedDataKey.Should().BeNull();
        cache.Should().OnlyContain(value => value == 0);
        key.Should().OnlyContain(value => value == 0);
        pendingUrl.Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public void Absolute_expiry_caps_sliding_activity_and_abandon_is_terminal()
    {
        var protectedUrl = new byte[] { 1 };
        var verifier = new byte[] { 2 };
        var connection = BulkSourceConnection.Begin(
            "conn-1", "actor-1", "job-1", "url-hash", protectedUrl, "state", verifier,
            "/jobs/job-1", Now, Idle, Absolute);

        connection.Touch(Now + TimeSpan.FromMinutes(110), Idle, Absolute);
        connection.ExpiresAt.Should().Be(Now + Absolute);
        connection.Abandon(Now + TimeSpan.FromMinutes(111));
        protectedUrl.Should().OnlyContain(value => value == 0);
        verifier.Should().OnlyContain(value => value == 0);
        connection.Status.Should().Be(BulkSourceConnectionStatus.Abandoned);
        var repeat = () => connection.Abandon(Now + TimeSpan.FromMinutes(112));
        repeat.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://app.example/jobs/job-1")]
    [InlineData("//app.example/jobs/job-1")]
    [InlineData(@"\jobs\job-1")]
    [InlineData("/jobs/job-1#fragment")]
    public void External_or_ambiguous_return_paths_are_rejected(string returnPath)
    {
        var action = () => BulkSourceConnection.Begin(
            "conn-1", "actor-1", "job-1", "url-hash", [1], "state", [2],
            returnPath, Now, Idle, Absolute);

        action.Should().Throw<ArgumentException>()
            .WithParameterName("redirectAfterPath");
    }

    private static string ToSnakeCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));
}
