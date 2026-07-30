using Bunit;
using FluentAssertions;
using TalentMatch.Web.Client.Components;

namespace TalentMatch.Web.Tests;

public class DocumentViewerTests : BunitContext
{
    [Fact]
    public void PdfWithEmbeddedContent_UsesDataUrl()
    {
        var document = new DocumentViewer.DocumentDto
        {
            Id = "document-1",
            FileName = "candidate.pdf",
            FileType = "application/pdf",
            ContentBase64 = "JVBERi0xLjQ="
        };

        var cut = Render<DocumentViewer>(parameters => parameters
            .Add(component => component.ApplicationId, "application-1")
            .Add(component => component.Documents, [document]));

        var iframe = cut.Find("iframe");
        iframe.GetAttribute("src").Should().Be("data:application/pdf;base64,JVBERi0xLjQ=");
        cut.Markup.Should().NotContain("/api/applications/");
    }
}