using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using Xunit;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class EligibilityEvaluatorTests
{
    private sealed class Source(bool docxConvertible) : ISharePointSource
    {
        public bool CanConvertToPdf(string extension) => docxConvertible && extension is ".docx" or ".md" or ".markdown";
        public Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SourceItemMetadata?> GetItemMetadataAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> DownloadAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static SourceFile File(string name, long size, bool package = false) =>
        new("d", "i", name, "/", size, "e", null, package);

    public static IEnumerable<object[]> Cases() =>
        BulkFixtures.Contract.GetProperty("eligibility").GetProperty("cases").EnumerateArray()
            .Select(c => new object[]
            {
                c.GetProperty("name").GetString()!,
                c.GetProperty("fileName").GetString()!,
                c.GetProperty("sizeBytes").GetInt64(),
                c.GetProperty("rasterize").GetBoolean(),
                c.TryGetProperty("docxConvertible", out var d) ? d.GetBoolean() : true,
                c.GetProperty("expect").GetString()!,
            });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Applies_every_shared_fixture_case(string name, string fileName, long size, bool rasterize, bool docxConvertible, string expect)
    {
        var limit = BulkFixtures.Contract.GetProperty("eligibility").GetProperty("perFileLimit").GetInt64();

        var result = new EligibilityEvaluator(new Source(docxConvertible)).Evaluate(File(fileName, size), limit, rasterize);

        var actual = result.Included ? "included" : "excluded:" + result.Exclusion!.Value.ToWire();
        actual.Should().Be(expect, name);
    }

    [Fact]
    public void Txt_is_exempt_from_conversion_and_never_flagged()
    {
        var evaluator = new EligibilityEvaluator(new Source(docxConvertible: false));

        var result = evaluator.Evaluate(File("cv.txt", 10), 1000, rasterize: true);

        result.Included.Should().BeTrue();
        result.ExcludedWhenRasterized.Should().BeFalse();
    }

    [Fact]
    public void Convertibility_is_reported_even_when_rasterize_is_off_so_the_toggle_needs_no_rescan()
    {
        var evaluator = new EligibilityEvaluator(new Source(docxConvertible: false));

        var off = evaluator.Evaluate(File("cv.docx", 10), 1000, rasterize: false);
        var on = evaluator.Evaluate(File("cv.docx", 10), 1000, rasterize: true);

        off.Included.Should().BeTrue();
        off.ExcludedWhenRasterized.Should().BeTrue();
        on.Included.Should().BeFalse();
        on.Exclusion.Should().Be(BulkReasonCode.NotConvertible);
    }

    [Fact]
    public void Packages_and_remote_items_are_unsupported_and_the_extension_is_authoritative()
    {
        var evaluator = new EligibilityEvaluator(new Source(true));

        evaluator.Evaluate(File("notebook.pdf", 10, package: true), 1000, false).Exclusion.Should().Be(BulkReasonCode.UnsupportedType);
        var lied = new SourceFile("d", "i", "payload.zip", "/", 10, "e", "application/pdf", false);
        evaluator.Evaluate(lied, 1000, false).Exclusion.Should().Be(BulkReasonCode.UnsupportedType);
    }

    [Theory]
    [InlineData("application/pdf", "%PDF-1.4", true)]
    [InlineData("application/pdf", "MZ......", false)]
    [InlineData("text/plain", "hello", true)]
    [InlineData("text/markdown", "# title", true)]
    public void Content_must_match_its_declared_type(string mime, string text, bool expected)
    {
        EligibilityEvaluator.ContentMatchesType(mime, System.Text.Encoding.ASCII.GetBytes(text)).Should().Be(expected);
    }

    [Fact]
    public void Binary_content_does_not_pass_as_text_and_empty_content_never_matches()
    {
        EligibilityEvaluator.ContentMatchesType("text/plain", [0x41, 0x00, 0x42]).Should().BeFalse();
        EligibilityEvaluator.ContentMatchesType("image/png", []).Should().BeFalse();
        EligibilityEvaluator.ContentMatchesType("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D]).Should().BeTrue();
        EligibilityEvaluator.ContentMatchesType("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0]).Should().BeTrue();
        EligibilityEvaluator.ContentMatchesType("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [0x50, 0x4B, 0x03, 0x04]).Should().BeTrue();
    }
}
