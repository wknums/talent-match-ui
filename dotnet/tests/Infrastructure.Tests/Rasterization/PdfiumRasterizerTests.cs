using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using FluentAssertions;
using SkiaSharp;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Rasterization;

namespace TalentMatch.Infrastructure.Tests.Rasterization;

public sealed class PdfiumRasterizerTests : IDisposable
{
    private const string Pdf = "application/pdf";
    private static readonly RasterizationOptions Defaults = new(150, 80, 30, TimeSpan.FromSeconds(60));

    private readonly PdfiumRasterizer _rasterizer = new(new BulkScoringOptions { ConvertConcurrency = 2 });

    public void Dispose() => _rasterizer.Dispose();

    [Fact]
    public async Task A_two_page_pdf_keeps_page_count_order_and_size_and_loses_its_text_layer()
    {
        var source = MinimalPdf((612, 792), (300, 400));
        ReadPages(source).Should().OnlyContain(p => p.Text.Contains("Page"), "the input has a text layer");

        var result = await _rasterizer.RasterizeAsync(source, Pdf, Defaults);

        result.PageCount.Should().Be(2);
        Encoding.ASCII.GetString(result.PdfBytes, 0, 5).Should().Be("%PDF-");
        var pages = ReadPages(result.PdfBytes);
        pages.Should().HaveCount(2);
        pages[0].Width.Should().BeApproximately(612, 1);
        pages[0].Height.Should().BeApproximately(792, 1);
        pages[1].Width.Should().BeApproximately(300, 1, "page order and sizes are preserved");
        pages[1].Height.Should().BeApproximately(400, 1);
        pages.Should().OnlyContain(p => string.IsNullOrWhiteSpace(p.Text), "the output is image-only");
    }

    [Fact]
    public async Task The_rendered_page_is_not_blank_and_keeps_a_white_background()
    {
        var result = await _rasterizer.RasterizeAsync(MinimalPdf((612, 792)), Pdf, Defaults);

        var page = ReadPages(result.PdfBytes, scale: 1d).Single();
        page.Corner.Should().BeGreaterThan(240, "the page background is white, not black");
        page.Darkest.Should().BeLessThan(120, "the original text was drawn into the image");
    }

    [Fact]
    public async Task A_png_becomes_a_one_page_pdf_sized_by_the_dpi_and_transparency_is_flattened_to_white()
    {
        var png = Image(SKEncodedImageFormat.Png, 300, 200, SKColors.Transparent);

        var result = await _rasterizer.RasterizeAsync(png, "image/png", Defaults);

        result.PageCount.Should().Be(1);
        var page = ReadPages(result.PdfBytes, scale: 1d).Single();
        page.Width.Should().BeApproximately(300 * 72d / 150, 1);
        page.Height.Should().BeApproximately(200 * 72d / 150, 1);
        page.Corner.Should().BeGreaterThan(240, "transparent pixels must not turn black");
        page.Text.Should().BeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_jpeg_is_embedded_without_being_re_encoded()
    {
        var jpeg = Image(SKEncodedImageFormat.Jpeg, 400, 300, SKColors.White);

        var result = await _rasterizer.RasterizeAsync(jpeg, "image/jpeg", Defaults);

        result.PageCount.Should().Be(1);
        IndexOf(result.PdfBytes, jpeg).Should().BeGreaterThanOrEqualTo(0, "the original JPEG bytes are carried through");
        var page = ReadPages(result.PdfBytes).Single();
        page.Width.Should().BeApproximately(400 * 72d / 150, 1);
    }

    [Fact]
    public async Task The_small_synthetic_fixture_files_convert()
    {
        var files = FixtureFiles();
        foreach (var (name, mime) in new[] { ("pdf-1page.pdf", Pdf), ("pdf-2page.pdf", Pdf) })
        {
            var result = await _rasterizer.RasterizeAsync(File.ReadAllBytes(Path.Combine(files, name)), mime, Defaults);
            result.PageCount.Should().Be(name.Contains("2page") ? 2 : 1, name);
        }
    }

    [Theory]
    [InlineData("corrupt.pdf")]
    [InlineData("encrypted.pdf")]
    public async Task A_corrupt_or_encrypted_pdf_fails_with_conversion_failed(string fixture)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureFiles(), fixture));

        var act = () => _rasterizer.RasterizeAsync(bytes, Pdf, Defaults);

        (await act.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConversionFailed);
    }

    [Fact]
    public async Task A_broken_image_fails_with_conversion_failed()
    {
        var act = () => _rasterizer.RasterizeAsync([1, 2, 3, 4, 5], "image/png", Defaults);

        (await act.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConversionFailed);
    }

    [Fact]
    public async Task More_pages_than_the_cap_fails_with_limit_exceeded()
    {
        var source = MinimalPdf((612, 792), (612, 792), (612, 792));

        var act = () => _rasterizer.RasterizeAsync(source, Pdf, Defaults with { MaxPages = 2 });

        (await act.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.LimitExceeded);
    }

    [Fact]
    public async Task A_page_over_the_pixel_budget_fails_with_limit_exceeded()
    {
        var act = () => _rasterizer.RasterizeAsync(MinimalPdf((15000, 15000)), Pdf, Defaults);

        (await act.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.LimitExceeded);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("text/markdown")]
    public async Task Other_types_never_fall_back_to_the_original(string mimeType)
    {
        var act = () => _rasterizer.RasterizeAsync(Encoding.UTF8.GetBytes("hello"), mimeType, Defaults);

        (await act.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConversionFailed);
    }

    [Fact]
    public async Task Running_out_of_time_fails_with_conversion_timeout()
    {
        var act = () => _rasterizer.RasterizeAsync(MinimalPdf((612, 792), (612, 792)), Pdf, Defaults with { Timeout = TimeSpan.Zero });

        (await act.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConversionTimeout);
    }

    [Fact]
    public async Task Cancelling_the_caller_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _rasterizer.RasterizeAsync(MinimalPdf((612, 792)), Pdf, Defaults, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Parallel_conversions_are_safe_and_give_the_same_result()
    {
        var source = MinimalPdf((612, 792), (612, 792));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _rasterizer.RasterizeAsync(source, Pdf, Defaults)));

        results.Should().OnlyContain(r => r.PageCount == 2);
        results.Select(r => ReadPages(r.PdfBytes).Count).Should().OnlyContain(c => c == 2);
    }

    private sealed record PageInfo(double Width, double Height, string Text, int Corner, int Darkest);

    // Reads a PDF back through PDFium: size in points at scale 1, its text layer, the top-left pixel and the darkest pixel.
    private static List<PageInfo> ReadPages(byte[] pdf, double scale = 1d)
    {
        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(scale));
        var pages = new List<PageInfo>();
        for (var i = 0; i < reader.GetPageCount(); i++)
        {
            using var page = reader.GetPageReader(i);
            var bgra = page.GetImage();
            var darkest = 255;
            for (var p = 0; p + 3 < bgra.Length; p += 4)
                darkest = Math.Min(darkest, Math.Min(bgra[p], Math.Min(bgra[p + 1], bgra[p + 2])));
            var corner = (bgra[0] + bgra[1] + bgra[2]) / 3;
            pages.Add(new PageInfo(page.GetPageWidth() / scale, page.GetPageHeight() / scale, page.GetText(), corner, darkest));
        }
        return pages;
    }

    private static byte[] Image(SKEncodedImageFormat format, int width, int height, SKColor background)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(20, 20, width / 3f, height / 3f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    private static string FixtureFiles()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "bulk-scoring-requests", "files");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("tests/fixtures/bulk-scoring-requests/files was not found.");
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    // A valid PDF whose pages are the given sizes (points) and each carry a line of text.
    private static byte[] MinimalPdf(params (int Width, int Height)[] pages)
    {
        var objects = new List<string> { "", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var pageIds = new List<int>();
        for (var i = 0; i < pages.Length; i++)
        {
            var text = $"BT /F1 24 Tf 72 {pages[i].Height - 100} Td (Page {i + 1} of the candidate CV) Tj ET";
            objects.Add($"<< /Length {text.Length} >>\nstream\n{text}\nendstream");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pages[i].Width} {pages[i].Height}] /Contents {objects.Count} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            pageIds.Add(objects.Count);
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R >>";
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pages.Length} >>";

        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(body.Length);
            body.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = body.Length;
        body.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) body.Append($"{offset:D10} 00000 n \n");
        body.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(body.ToString());
    }
}
