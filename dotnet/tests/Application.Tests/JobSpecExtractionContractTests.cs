using System.Text.Json;
using FluentAssertions;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Application.JobExtraction.Services;
using TalentMatch.Application.Rubrics.Models;
using TalentMatch.Application.Rubrics.Services;

namespace TalentMatch.Application.Tests;

public sealed class JobSpecExtractionContractTests
{
    private readonly JobSpecExtractionContractValidator _validator = new(new RubricOrderNormalizer());
    private readonly LegacyRubricAdapter _legacyRubricAdapter = new(new RubricOrderNormalizer());

    [Fact]
    public void Validate_AcceptsValidItemizedFixture_AndNormalizesRubricV2()
    {
        var rawFixture = TestFixtureLoader.LoadFeatureFixtureText("valid-itemized-extraction.json");
        var expected = TestFixtureLoader.LoadFeatureFixture<RubricEnvelopeModel>("expected-valid-rubric-v2.json");

        var result = _validator.Validate(rawFixture);

        result.IsValid.Should().BeTrue();
        result.ValidationStatus.Should().Be("valid");
        result.Findings.Should().BeEmpty();
        result.Rubric.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Validate_RejectsMissingFieldFixture_WithActionableFindingCodes()
    {
        var rawFixture = TestFixtureLoader.LoadFeatureFixtureText("invalid-missing-fields.json");

        var result = _validator.Validate(rawFixture);

        result.IsValid.Should().BeFalse();
        result.Findings.Select(finding => finding.Code)
            .Should()
            .Contain(["schema_mismatch", "missing_source_trace"]);
    }

    [Fact]
    public void Validate_RejectsInvalidWeightFixture()
    {
        var rawFixture = TestFixtureLoader.LoadFeatureFixtureText("invalid-weight-total.json");

        var result = _validator.Validate(rawFixture);

        result.IsValid.Should().BeFalse();
        result.Findings.Should().ContainSingle(finding => finding.Code == "invalid_weight_total");
    }

    [Fact]
    public void Validate_PreservesNeedsReviewRequirement()
    {
        var rawFixture = TestFixtureLoader.LoadFeatureFixtureText("ambiguous-category.json");
        var expected = TestFixtureLoader.LoadFeatureFixture<RubricEnvelopeModel>("expected-ambiguous-rubric-v2.json");

        var result = _validator.Validate(rawFixture);

        result.IsValid.Should().BeTrue();
        result.Rubric.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        result.Rubric!.Items.Should().ContainSingle(item => item.ReviewStatus == RubricReviewStatuses.NeedsReview);
    }

    [Fact]
    public void LegacyConversion_CreatesDeterministicRubricEnvelope()
    {
        var legacyRubricJson = JsonSerializer.Serialize(new[]
        {
            new LegacyRubricCategoryModel { Name = "Technical Platform Skills", Weight = 0.6, Description = "Expert SQL experience; Expert Python experience" },
            new LegacyRubricCategoryModel { Name = "Collaboration and Communication", Weight = 0.4, Description = "Excellent written and verbal communication skills" },
        });
        var mustHavesJson = JsonSerializer.Serialize(new[]
        {
            new { Criterion = "Expert SQL experience", Description = "Expert SQL experience" },
            new { Criterion = "Expert Python experience", Description = "Expert Python experience" },
            new { Criterion = "Excellent written and verbal communication skills", Description = "Excellent written and verbal communication skills" },
        });

        var actual = _legacyRubricAdapter.CreateLegacyConversionProposal(
            legacyRubricJson,
            mustHavesJson,
            "[]",
            "legacy-config-v1");

        var expected = TestFixtureLoader.LoadFeatureFixture<RubricEnvelopeModel>("expected-legacy-conversion.json");
        actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }
}
