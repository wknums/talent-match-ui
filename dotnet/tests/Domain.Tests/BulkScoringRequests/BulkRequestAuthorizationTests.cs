using FluentAssertions;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Domain.Tests.BulkScoringRequests;

public sealed class BulkRequestAuthorizationTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Request_authorization_is_fully_bound_and_contains_no_stack_or_access_mode()
    {
        var type = typeof(BulkRequestAuthorization);
        string[] bindings =
        [
            "RequestId", "ProductActorId", "SourceTenantId", "SourceAccountBindingHash",
            "SourceDriveId", "SourceFolderItemId", "Payload", "WrappedDataKey", "KeyVersion",
        ];
        bindings.Should().AllSatisfy(name => type.GetProperty(name).Should().NotBeNull());
        type.GetProperty("RecruiterActorId").Should().BeNull();
        type.GetProperty("AccessModel").Should().BeNull();
        type.GetProperty("OwningStack").Should().BeNull();
    }

    [Fact]
    public void Request_authorization_registry_covers_lapse_revocation_access_loss_and_discard()
    {
        WireEnum.AllWireValues<BulkAuthorizationStatus>().Should().Equal(
            "active", "lapsed", "revoked", "accessLost", "discarded", "unavailableHistorical");
    }

    [Fact]
    public void Retry_binding_cannot_reuse_an_existing_request_authorization()
    {
        typeof(BulkRequestAuthorization).GetMethods().Select(method => method.Name)
            .Should().Contain("IsBoundTo")
            .And.Contain("MarkRevoked")
            .And.Contain("MarkAccessLost");
    }

    [Fact]
    public void Full_binding_use_rotation_and_unusable_transitions_clear_material()
    {
        var payload = new byte[] { 1, 2, 3 };
        var key = new byte[] { 4, 5, 6 };
        var authorization = BulkRequestAuthorization.Establish(
            "request-1", "actor-1", "tenant-1", "account-hash", "drive-1", "folder-1",
            payload, key, "key-v1", Now);

        authorization.IsBoundTo(
            "request-1", "actor-1", "tenant-1", "account-hash", "drive-1", "folder-1").Should().BeTrue();
        authorization.IsBoundTo(
            "request-2", "actor-1", "tenant-1", "account-hash", "drive-1", "folder-1").Should().BeFalse();
        authorization.RecordUse(Now.AddMinutes(1));

        var rotatedPayload = new byte[] { 7, 8 };
        var rotatedKey = new byte[] { 9, 10 };
        authorization.ReplaceProtectedMaterial(rotatedPayload, rotatedKey, "key-v2", Now.AddMinutes(2));
        payload.Should().OnlyContain(value => value == 0);
        key.Should().OnlyContain(value => value == 0);

        authorization.MarkRevoked("source_authorization_revoked", Now.AddMinutes(3));
        authorization.Status.Should().Be(BulkAuthorizationStatus.Revoked);
        authorization.Payload.Should().BeNull();
        authorization.WrappedDataKey.Should().BeNull();
        rotatedPayload.Should().OnlyContain(value => value == 0);
        rotatedKey.Should().OnlyContain(value => value == 0);
        var reuse = () => authorization.RecordUse(Now.AddMinutes(4));
        reuse.Should().Throw<InvalidOperationException>();
    }
}
