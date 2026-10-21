using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class Microsoft365PublisherGateTests
{
    [Theory]
    [InlineData("Production", true, true)]
    [InlineData("Production", false, false)]
    [InlineData("Production", null, false)]
    [InlineData("Development", false, true)]
    [InlineData("Development", null, true)]
    [InlineData("QA", false, true)]
    [InlineData("QA", null, true)]
    [InlineData("Staging", false, false)]
    [InlineData("Unknown", null, false)]
    public void Gate_depends_only_on_product_environment_and_explicit_assertion(
        string environment,
        bool? publisherVerified,
        bool expected)
    {
        var type = typeof(BulkScoringOptions).Assembly.GetType(
            "TalentMatch.Application.BulkScoringRequests.Microsoft365PublisherGate");
        type.Should().NotBeNull();
        var result = type!.GetMethod("CanActivate")!.Invoke(null, [environment, publisherVerified]);
        result.Should().Be(expected);
    }

    [Fact]
    public void Gate_signature_has_no_source_tenant_account_or_folder_input()
    {
        var type = typeof(BulkScoringOptions).Assembly.GetType(
            "TalentMatch.Application.BulkScoringRequests.Microsoft365PublisherGate");
        type.Should().NotBeNull();
        var parameters = type!.GetMethod("CanActivate")!.GetParameters();
        parameters.Select(parameter => parameter.Name).Should().Equal("productEnvironment", "publisherVerified");
    }
}
