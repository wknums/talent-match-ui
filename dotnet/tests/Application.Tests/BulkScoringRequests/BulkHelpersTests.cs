using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Services;
using Xunit;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class BulkHelpersTests
{
    [Fact]
    public void Deterministic_ids_match_the_rfc_4122_version_5_reference_vector()
    {
        // uuid5(NAMESPACE_DNS, "www.example.com") is a published reference value
        DeterministicIds.Create(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com")
            .Should().Be("2ed6657d-e927-568b-95e1-2665a8aea6a2");
    }

    [Fact]
    public void Ids_are_stable_distinct_per_purpose_and_valid_guids()
    {
        DeterministicIds.Application("item-1").Should().Be(DeterministicIds.Application("item-1"));
        DeterministicIds.Application("item-1").Should().NotBe(DeterministicIds.Application("item-2"));
        DeterministicIds.Application("item-1").Should().NotBe(DeterministicIds.Document("item-1"));
        var id = DeterministicIds.Application("item-1");
        id.Should().HaveLength(36);
        Guid.TryParse(id, out _).Should().BeTrue();
    }

    [Fact]
    public void Ids_carry_the_version_5_and_variant_bits()
    {
        var id = DeterministicIds.Create("anything");
        id[14].Should().Be('5');
        "89ab".Should().Contain(id[19].ToString());
    }

    [Fact]
    public void Fingerprint_candidate_set_holds_the_raw_and_the_legacy_base64_hash()
    {
        var bytes = "hello"u8.ToArray();

        ContentFingerprint.Raw(bytes).Should().Be("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
        ContentFingerprint.LegacyBase64(bytes).Should().Be(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("aGVsbG8="))));
        ContentFingerprint.Candidates(bytes).Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(1, 4, 6)]
    [InlineData(2, 8, 12)]
    [InlineData(3, 16, 24)]
    public void Backoff_grows_exponentially_within_the_jitter_band(int attempt, double minSeconds, double maxSeconds)
    {
        BulkRetryPolicy.NextDelay(attempt, nextRandom: () => 0.0).TotalSeconds.Should().BeApproximately(minSeconds, 0.01);
        BulkRetryPolicy.NextDelay(attempt, nextRandom: () => 1.0).TotalSeconds.Should().BeApproximately(maxSeconds, 0.01);
    }

    [Fact]
    public void Backoff_is_capped_at_five_minutes_and_honors_a_longer_retry_after()
    {
        BulkRetryPolicy.NextDelay(12, nextRandom: () => 1.0).Should().Be(BulkRetryPolicy.MaxDelay);
        BulkRetryPolicy.NextDelay(1, TimeSpan.FromSeconds(90), () => 0.5).Should().Be(TimeSpan.FromSeconds(90));
        BulkRetryPolicy.NextDelay(1, TimeSpan.FromSeconds(1), () => 0.5).TotalSeconds.Should().BeApproximately(5, 0.01);
    }

    [Fact]
    public void Attempts_are_exhausted_at_the_configured_maximum()
    {
        BulkRetryPolicy.IsExhausted(4, 5).Should().BeFalse();
        BulkRetryPolicy.IsExhausted(5, 5).Should().BeTrue();
    }
}
