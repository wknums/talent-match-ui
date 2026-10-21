using System.Text;
using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class SourceLinkClassifierTests
{
    private readonly SourceLinkClassifier _classifier = new();

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/hr/Shared%20Documents/Candidates", BulkSourceKind.SharePoint)]
    [InlineData("https://contoso-my.sharepoint.com/personal/user/Documents/Candidates", BulkSourceKind.OneDriveBusiness)]
    [InlineData("https://another-org.sharepoint.com/sites/recruiting", BulkSourceKind.SharePoint)]
    public void Accepts_structural_commercial_organizational_links(string url, BulkSourceKind expected)
    {
        _classifier.TryClassify(url, out var result).Should().BeTrue();
        result!.Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://1drv.ms/f/example")]
    [InlineData("https://onedrive.live.com/?id=personal")]
    [InlineData("https://tenant.sharepoint.us/sites/hr")]
    [InlineData("https://tenant.sharepoint.de/sites/hr")]
    [InlineData("https://sub.tenant.sharepoint.com/sites/hr")]
    [InlineData("https://127.0.0.1/sites/hr")]
    [InlineData("https://tenant.sharepoint.com:8443/sites/hr")]
    [InlineData("https://user@tenant.sharepoint.com/sites/hr")]
    [InlineData("https://tenant.sharepoint.com/sites/hr#fragment")]
    [InlineData("https://tenant.sharepoint.com/sites/%0Acandidates")]
    [InlineData("http://tenant.sharepoint.com/sites/hr")]
    public void Rejects_personal_noncommercial_and_unsafe_links_before_outbound_work(string url)
    {
        _classifier.TryClassify(url, out var result).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void Has_no_outbound_dependency_and_encodes_only_a_Graph_sharing_token()
    {
        typeof(SourceLinkClassifier).GetConstructors().Should().ContainSingle()
            .Which.GetParameters().Should().BeEmpty();
        var uri = new Uri("https://tenant.sharepoint.com/sites/hr/Shared%20Documents/Candidates");

        var token = SourceLinkClassifier.ToSharingToken(uri);

        token.Should().StartWith("u!");
        var encoded = token[2..].Replace('_', '/').Replace('-', '+');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Should().Be(uri.AbsoluteUri);
    }

    [Fact]
    public void Normalizes_root_site_library_navigation_links_structurally_without_allow_lists()
    {
        const string url =
            "https://xh0fm.sharepoint.com/Shared%20Documents/Forms/AllItems.aspx?id=%2FShared%20Documents%2FJob%2DOperator%5Flight%5FEquipment%5FJOB%2D20261001%2DCB2D86%2Dapplications&viewid=72b2edfc%2D1762%2D490f%2Dae46%2D32cb5f84960b";

        _classifier.TryClassify(url, out var result).Should().BeTrue();

        result!.Kind.Should().Be(BulkSourceKind.SharePoint);
        result.Uri.IdnHost.Should().Be("xh0fm.sharepoint.com");
        result.Uri.LocalPath.Should().Be(
            "/Shared Documents/Job-Operator_light_Equipment_JOB-20261001-CB2D86-applications");
        result.Uri.Query.Should().BeEmpty();
    }
}
