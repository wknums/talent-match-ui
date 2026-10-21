using System.Security.Cryptography;
using FluentAssertions;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.Tests.SharePoint;

public sealed class AuthorizationEnvelopeTests
{
    [Fact]
    public void Associated_data_mismatch_and_tampering_are_rejected()
    {
        var plaintext = "cache"u8.ToArray();
        var (payload, key) = AuthorizationEnvelope.Seal(plaintext, "bulk-source-request:req-1");

        var openWithWrongBinding = () => AuthorizationEnvelope.Open(
            payload, key, "bulk-source-connection:conn-1:actor-1:job-1:url-hash");
        openWithWrongBinding.Should().Throw<CryptographicException>();

        payload[^1] ^= 0x01;
        var openTampered = () => AuthorizationEnvelope.Open(
            payload, key, "bulk-source-request:req-1");
        openTampered.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Every_seal_uses_a_fresh_data_key_and_discard_can_zero_material()
    {
        var first = AuthorizationEnvelope.Seal("cache"u8.ToArray(), "bulk-source-request:req-1");
        var second = AuthorizationEnvelope.Seal("cache"u8.ToArray(), "bulk-source-request:req-1");
        first.DataKey.Should().NotEqual(second.DataKey);
        first.Payload.Should().NotEqual(second.Payload);

        AuthorizationEnvelope.Discard(first.Payload, first.DataKey);
        first.Payload.Should().OnlyContain(value => value == 0);
        first.DataKey.Should().OnlyContain(value => value == 0);
    }
}
