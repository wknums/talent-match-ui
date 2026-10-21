using Docnet.Core;
using Docnet.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Infrastructure.Rasterization;

// Turns a PDF, PNG or JPEG into an image-only PDF (no text layer): PDFium renders each page, SkiaSharp
// compresses it to JPEG, PDFsharp assembles the pages at their original size. Any other type fails;
// there is never a silent fallback to the original (FR-010D). Word and Markdown arrive here already
// converted to PDF by the source (Graph format=pdf).
public sealed partial class PdfiumRasterizer : IDocumentRasterizer, IDisposable
{
    private const string Pdf = "application/pdf";
    private const string Png = "image/png";
    private const string Jpeg = "image/jpeg";
    private const double PointsPerInch = 72d;

    // A0 at 150 DPI is about 35 megapixels; anything above this is not a CV.
    private const long MaxPixelsPerPage = 40_000_000;

    private readonly SemaphoreSlim _slots;

    public PdfiumRasterizer(BulkScoringOptions options) =>
        _slots = new SemaphoreSlim(Math.Max(1, options.ConvertConcurrency));

    public async Task<RasterizationResult> RasterizeAsync(
        byte[] source,
        string mimeType,
        RasterizationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (mimeType is not (Pdf or Png or Jpeg))
            throw new BulkConversionException(BulkReasonCode.ConversionFailed, $"Files of type {mimeType} cannot be converted to an image-based PDF.");

        // Waiting for a slot does not count against the per-file time budget.
        await _slots.WaitAsync(cancellationToken);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(options.Timeout);
            var token = budget.Token;
            try
            {
                var converted = await Task.Run(
                    () => mimeType == Pdf ? RenderPdf(source, options, token) : WrapImage(source, options, token),
                    token);
                return await RasterFidelityAdmission.ValidateAsync(
                    source,
                    converted.PdfBytes,
                    Pdf,
                    options,
                    long.MaxValue,
                    TimeProvider.System,
                    mimeType,
                    token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BulkConversionException(BulkReasonCode.ConversionTimeout, "Converting the file took too long.");
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}

public static class RasterFidelityAdmission
            {
                private const string Pdf = "application/pdf";
                private const string Png = "image/png";
                private const string Jpeg = "image/jpeg";
                private const double PointsPerInch = 72d;
                private const byte InkThreshold = 245;
                private const double MinimumInkRatio = 0.0005;
                private const double EdgeMargin = 0.005;
                private const double MinimumBoundingBoxAreaRatio = 0.90;
                private const double MinimumSsim = 0.95;
                private const int WindowRadius = 5;
                private static readonly float[] GaussianKernel = CreateGaussianKernel();

                public static Task<RasterizationResult> ValidateAsync(
                    byte[] source,
                    byte[] output,
                    string outputMediaType,
                    RasterizationOptions options,
                    long maxBytes,
                    TimeProvider clock,
                    string sourceMediaType = Pdf,
                    CancellationToken cancellationToken = default) =>
                    Task.Run(
                        () => Validate(source, output, outputMediaType, options, maxBytes, clock, sourceMediaType, cancellationToken),
                        cancellationToken);

                private static RasterizationResult Validate(
                    byte[] source,
                    byte[] output,
                    string outputMediaType,
                    RasterizationOptions options,
                    long maxBytes,
                    TimeProvider clock,
                    string sourceMediaType,
                    CancellationToken cancellationToken)
                {
                    if (!string.Equals(outputMediaType, Pdf, StringComparison.OrdinalIgnoreCase))
                        throw Rejected(BulkReasonCode.ConvertedTypeInvalid, "The converted document type is invalid.");
                    if (output.LongLength <= 0 || output.LongLength > maxBytes)
                        throw Rejected(BulkReasonCode.ConvertedTooLarge, "The converted document is larger than the allowed size.");
                    if (!HasPdfSignature(output))
                        throw Rejected(BulkReasonCode.ConvertedMagicInvalid, "The converted document does not have a valid PDF signature.");

                    IReadOnlyList<RasterPage> sourcePages;
                    IReadOnlyList<RasterPage> outputPages;
                    try
                    {
                        sourcePages = ReadSourcePages(source, sourceMediaType, options.Dpi, cancellationToken);
                        outputPages = ReadPdfPages(output, options.Dpi, cancellationToken);
                    }
                    catch (BulkConversionException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new BulkConversionException(
                            BulkReasonCode.ConvertedDocumentInvalid,
                            "The converted PDF is structurally invalid.",
                            ex);
                    }

                    if (outputPages.Count <= 0)
                        throw Rejected(BulkReasonCode.ConversionPageInvalid, "The converted document has no valid pages.");
                    if (sourcePages.Count <= 0)
                        throw Rejected(BulkReasonCode.ConversionFailed, "The source document has no valid pages.");
                    if (sourcePages.Count != outputPages.Count)
                        throw Rejected(BulkReasonCode.ConversionPageMismatch, "The converted document page count does not match the source.");

                    var minimumSsim = 1d;
                    var pageSsim = new List<double>(sourcePages.Count);
                    for (var index = 0; index < sourcePages.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sourcePage = sourcePages[index];
                        var outputPage = outputPages[index];
                        if (sourcePage.Width <= 0 || sourcePage.Height <= 0 || outputPage.Width <= 0 || outputPage.Height <= 0)
                            throw Rejected(BulkReasonCode.ConversionPageInvalid, $"Converted page {index + 1} is invalid.");
                        if (Math.Abs(sourcePage.WidthPoints - outputPage.WidthPoints) > 1d
                            || Math.Abs(sourcePage.HeightPoints - outputPage.HeightPoints) > 1d)
                            throw Rejected(BulkReasonCode.ConversionDimensionMismatch, $"Converted page {index + 1} dimensions do not match the source.");

                        var sourceMetrics = PageMetrics.From(sourcePage);
                        var outputMetrics = PageMetrics.From(outputPage);
                        if (sourceMetrics.InkRatio >= MinimumInkRatio && outputMetrics.InkRatio < MinimumInkRatio)
                            throw Rejected(BulkReasonCode.ConversionBlankPage, $"Converted page {index + 1} is blank.");
                        if (IsCropped(sourceMetrics, outputMetrics))
                            throw Rejected(BulkReasonCode.ConversionCroppedPage, $"Converted page {index + 1} is cropped.");
                        if (sourcePage.Width != outputPage.Width || sourcePage.Height != outputPage.Height)
                            throw Rejected(BulkReasonCode.ConversionDimensionMismatch, $"Converted page {index + 1} dimensions do not match the source.");

                        var ssim = StructuralSimilarity(sourcePage.Gray, outputPage.Gray, sourcePage.Width, sourcePage.Height);
                        pageSsim.Add(ssim);
                        minimumSsim = Math.Min(minimumSsim, ssim);
                        if (ssim < MinimumSsim)
                            throw Rejected(BulkReasonCode.ConversionSsimBelowThreshold, $"Converted page {index + 1} does not meet the fidelity threshold.");
                    }

                    return new RasterizationResult(output, outputPages.Count)
                    {
                        MediaType = Pdf,
                        OriginalPageCount = sourcePages.Count,
                        RasterDpiUsed = options.Dpi,
                        MinimumPageSsim = minimumSsim,
                        PageSsim = pageSsim,
                        FidelityValidatedAt = clock.GetUtcNow().UtcDateTime,
                    };
                }

                private static IReadOnlyList<RasterPage> ReadSourcePages(byte[] source, string mediaType, int dpi, CancellationToken cancellationToken) =>
                    mediaType switch
                    {
                        Pdf => ReadPdfPages(source, dpi, cancellationToken),
                        Png or Jpeg => [ReadImagePage(source, dpi)],
                        _ => throw Rejected(BulkReasonCode.ConvertedTypeInvalid, "The conversion source type is invalid."),
                    };

                private static IReadOnlyList<RasterPage> ReadPdfPages(byte[] pdf, int dpi, CancellationToken cancellationToken)
                {
                    var scale = dpi / PointsPerInch;
                    using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(scale));
                    var count = reader.GetPageCount();
                    var pages = new List<RasterPage>(Math.Max(0, count));
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var page = reader.GetPageReader(index);
                        var width = page.GetPageWidth();
                        var height = page.GetPageHeight();
                        pages.Add(new(
                            width,
                            height,
                            width / scale,
                            height / scale,
                            ToGray(page.GetImage(), width, height)));
                    }
                    return pages;
                }

                private static RasterPage ReadImagePage(byte[] source, int dpi)
                {
                    using var bitmap = SKBitmap.Decode(source)
                        ?? throw Rejected(BulkReasonCode.ConversionFailed, "The source image could not be read.");
                    var width = bitmap.Width;
                    var height = bitmap.Height;
                    var gray = new byte[width * height];
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var color = bitmap.GetPixel(x, y);
                        var alpha = color.Alpha / 255d;
                        var red = color.Red * alpha + 255d * (1d - alpha);
                        var green = color.Green * alpha + 255d * (1d - alpha);
                        var blue = color.Blue * alpha + 255d * (1d - alpha);
                        gray[y * width + x] = (byte)Math.Clamp((int)Math.Round(0.299 * red + 0.587 * green + 0.114 * blue), 0, 255);
                    }
                    return new(width, height, width * PointsPerInch / dpi, height * PointsPerInch / dpi, gray);
                }

                private static byte[] ToGray(byte[] bgra, int width, int height)
                {
                    if (bgra.Length < width * height * 4)
                        throw Rejected(BulkReasonCode.ConversionPageInvalid, "A converted page could not be rendered.");
                    var gray = new byte[width * height];
                    for (var pixel = 0; pixel < gray.Length; pixel++)
                    {
                        var offset = pixel * 4;
                        var alpha = bgra[offset + 3] / 255d;
                        var blue = bgra[offset] * alpha + 255d * (1d - alpha);
                        var green = bgra[offset + 1] * alpha + 255d * (1d - alpha);
                        var red = bgra[offset + 2] * alpha + 255d * (1d - alpha);
                        gray[pixel] = (byte)Math.Clamp((int)Math.Round(0.299 * red + 0.587 * green + 0.114 * blue), 0, 255);
                    }
                    return gray;
                }

                private static bool HasPdfSignature(byte[] bytes)
                {
                    if (bytes.Length < 10 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return false;
                    var end = bytes.Length - 1;
                    while (end >= 0 && char.IsWhiteSpace((char)bytes[end])) end--;
                    return end >= 4 && bytes.AsSpan(end - 4, 5).SequenceEqual("%%EOF"u8);
                }

                private static bool IsCropped(PageMetrics source, PageMetrics output)
                {
                    if (output.Box is null) return source.Box is not null;
                    var newlyTouchesEdge = TouchesEdge(output.Box.Value)
                        && source.Box is { } sourceBox
                        && !TouchesEdge(sourceBox);
                    if (newlyTouchesEdge) return true;
                    if (source.Box is not { } sourceBounds) return false;
                    var sourceArea = sourceBounds.Area;
                    return sourceArea > 0 && output.Box.Value.Area / sourceArea < MinimumBoundingBoxAreaRatio;
                }

                private static bool TouchesEdge(NormalizedBox box) =>
                    box.Left <= EdgeMargin || box.Top <= EdgeMargin
                    || box.Right >= 1d - EdgeMargin || box.Bottom >= 1d - EdgeMargin;

                private static double StructuralSimilarity(byte[] first, byte[] second, int width, int height)
                {
                    var firstValues = first.Select(value => (float)value).ToArray();
                    var secondValues = second.Select(value => (float)value).ToArray();
                    var firstMean = Gaussian(firstValues, width, height);
                    var secondMean = Gaussian(secondValues, width, height);
                    var firstSquare = Gaussian(firstValues.Select(value => value * value).ToArray(), width, height);
                    var secondSquare = Gaussian(secondValues.Select(value => value * value).ToArray(), width, height);
                    var cross = Gaussian(firstValues.Zip(secondValues, (left, right) => left * right).ToArray(), width, height);
                    const double c1 = 6.5025;
                    const double c2 = 58.5225;
                    var sum = 0d;
                    var count = 0;
                    for (var y = WindowRadius; y < height - WindowRadius; y++)
                    for (var x = WindowRadius; x < width - WindowRadius; x++)
                    {
                        var index = y * width + x;
                        var mean1 = firstMean[index];
                        var mean2 = secondMean[index];
                        var variance1 = Math.Max(0d, firstSquare[index] - mean1 * mean1);
                        var variance2 = Math.Max(0d, secondSquare[index] - mean2 * mean2);
                        var covariance = cross[index] - mean1 * mean2;
                        sum += ((2d * mean1 * mean2 + c1) * (2d * covariance + c2))
                            / ((mean1 * mean1 + mean2 * mean2 + c1) * (variance1 + variance2 + c2));
                        count++;
                    }
                    return count == 0 ? 0d : sum / count;
                }

                private static float[] Gaussian(float[] input, int width, int height)
                {
                    var horizontal = new float[input.Length];
                    var output = new float[input.Length];
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var value = 0f;
                        for (var offset = -WindowRadius; offset <= WindowRadius; offset++)
                            value += input[y * width + Reflect(x + offset, width)] * GaussianKernel[offset + WindowRadius];
                        horizontal[y * width + x] = value;
                    }
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var value = 0f;
                        for (var offset = -WindowRadius; offset <= WindowRadius; offset++)
                            value += horizontal[Reflect(y + offset, height) * width + x] * GaussianKernel[offset + WindowRadius];
                        output[y * width + x] = value;
                    }
                    return output;
                }

                private static int Reflect(int value, int size)
                {
                    while (value < 0 || value >= size)
                        value = value < 0 ? -value - 1 : 2 * size - value - 1;
                    return value;
                }

                private static float[] CreateGaussianKernel()
                {
                    const double sigma = 1.5;
                    var kernel = Enumerable.Range(-WindowRadius, WindowRadius * 2 + 1)
                        .Select(value => (float)Math.Exp(-(value * value) / (2d * sigma * sigma)))
                        .ToArray();
                    var sum = kernel.Sum();
                    for (var index = 0; index < kernel.Length; index++) kernel[index] /= sum;
                    return kernel;
                }

                private static BulkConversionException Rejected(BulkReasonCode reason, string message) => new(reason, message);

                private sealed record RasterPage(int Width, int Height, double WidthPoints, double HeightPoints, byte[] Gray);

                private readonly record struct NormalizedBox(double Left, double Top, double Right, double Bottom)
                {
                    public double Area => Math.Max(0d, Right - Left) * Math.Max(0d, Bottom - Top);
                }

                private sealed record PageMetrics(double InkRatio, NormalizedBox? Box)
                {
                    public static PageMetrics From(RasterPage page)
                    {
                        var ink = 0;
                        var minX = page.Width;
                        var minY = page.Height;
                        var maxX = -1;
                        var maxY = -1;
                        for (var y = 0; y < page.Height; y++)
                        for (var x = 0; x < page.Width; x++)
                        {
                            if (page.Gray[y * page.Width + x] >= InkThreshold) continue;
                            ink++;
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                        }
                        var box = ink == 0
                            ? (NormalizedBox?)null
                            : new(
                                minX / (double)page.Width,
                                minY / (double)page.Height,
                                (maxX + 1d) / page.Width,
                                (maxY + 1d) / page.Height);
                        return new(ink / (double)page.Gray.Length, box);
                    }
                }
            }

public sealed partial class PdfiumRasterizer
{
    private static RasterizationResult RenderPdf(byte[] source, RasterizationOptions options, CancellationToken token)
    {
        var scale = options.Dpi / PointsPerInch;
        var keepAlive = new List<IDisposable>();
        try
        {
            using var reader = DocLib.Instance.GetDocReader(source, new PageDimensions(scale));
            var pages = reader.GetPageCount();
            if (pages <= 0)
                throw new BulkConversionException(BulkReasonCode.ConversionFailed, "The document has no pages.");
            if (pages > options.MaxPages)
                throw new BulkConversionException(BulkReasonCode.LimitExceeded, $"The document has {pages} pages; the maximum is {options.MaxPages}.");

            using var output = new PdfDocument();
            using var geometry = PdfReader.Open(new MemoryStream(source), PdfDocumentOpenMode.Import);
            if (geometry.PageCount != pages)
                throw new BulkConversionException(BulkReasonCode.ConversionPageMismatch, "The source page structure is inconsistent.");
            for (var index = 0; index < pages; index++)
            {
                token.ThrowIfCancellationRequested();
                using var page = reader.GetPageReader(index);
                var width = page.GetPageWidth();
                var height = page.GetPageHeight();
                if (width <= 0 || height <= 0)
                    throw new BulkConversionException(BulkReasonCode.ConversionFailed, $"Page {index + 1} has no size.");
                if ((long)width * height > MaxPixelsPerPage)
                    throw new BulkConversionException(BulkReasonCode.LimitExceeded, $"Page {index + 1} is too large to convert.");

                var jpeg = EncodeRendered(page.GetImage(), width, height, options.JpegQuality);
                AddImagePage(output, jpeg, geometry.Pages[index].Width.Point, geometry.Pages[index].Height.Point, keepAlive);
            }

            return new RasterizationResult(Save(output), pages);
        }
        catch (Exception ex) when (ex is not (BulkConversionException or OperationCanceledException))
        {
            throw new BulkConversionException(BulkReasonCode.ConversionFailed, "The document could not be converted to an image-based PDF.", ex);
        }
        finally
        {
            foreach (var disposable in keepAlive) disposable.Dispose();
        }
    }

    private static RasterizationResult WrapImage(byte[] source, RasterizationOptions options, CancellationToken token)
    {
        var keepAlive = new List<IDisposable>();
        try
        {
            token.ThrowIfCancellationRequested();
            using var codec = SKCodec.Create(new MemoryStream(source))
                ?? throw new BulkConversionException(BulkReasonCode.ConversionFailed, "The image could not be read.");
            var width = codec.Info.Width;
            var height = codec.Info.Height;
            if (width <= 0 || height <= 0)
                throw new BulkConversionException(BulkReasonCode.ConversionFailed, "The image has no size.");
            if ((long)width * height > MaxPixelsPerPage)
                throw new BulkConversionException(BulkReasonCode.LimitExceeded, "The image is too large to convert.");

            // A JPEG is embedded as it is (not re-encoded). Everything else becomes a flattened JPEG.
            var jpeg = codec.EncodedFormat == SKEncodedImageFormat.Jpeg ? source : EncodeDecoded(source, options.JpegQuality);
            var widthPoints = width * PointsPerInch / options.Dpi;
            var heightPoints = height * PointsPerInch / options.Dpi;

            using var output = new PdfDocument();
            try
            {
                AddImagePage(output, jpeg, widthPoints, heightPoints, keepAlive);
                return new RasterizationResult(Save(output), 1);
            }
            catch (Exception) when (codec.EncodedFormat == SKEncodedImageFormat.Jpeg)
            {
                // Unusual JPEG variants (for example CMYK) are decoded and re-encoded instead.
                foreach (var disposable in keepAlive) disposable.Dispose();
                keepAlive.Clear();
                using var retry = new PdfDocument();
                AddImagePage(retry, EncodeDecoded(source, options.JpegQuality), widthPoints, heightPoints, keepAlive);
                return new RasterizationResult(Save(retry), 1);
            }
        }
        catch (Exception ex) when (ex is not (BulkConversionException or OperationCanceledException))
        {
            throw new BulkConversionException(BulkReasonCode.ConversionFailed, "The image could not be converted to an image-based PDF.", ex);
        }
        finally
        {
            foreach (var disposable in keepAlive) disposable.Dispose();
        }
    }

    // PDFium returns unpremultiplied BGRA with a transparent page background; it is composited onto white
    // in place first, otherwise the JPEG (which has no alpha) would come out black.
    private static byte[] EncodeRendered(byte[] bgra, int width, int height, int quality)
    {
        for (var p = 0; p + 3 < bgra.Length; p += 4)
        {
            var alpha = bgra[p + 3];
            if (alpha == 255) continue;
            var inverse = 255 - alpha;
            bgra[p] = (byte)((bgra[p] * alpha + 255 * inverse + 127) / 255);
            bgra[p + 1] = (byte)((bgra[p + 1] * alpha + 255 * inverse + 127) / 255);
            bgra[p + 2] = (byte)((bgra[p + 2] * alpha + 255 * inverse + 127) / 255);
            bgra[p + 3] = 255;
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, bgra);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new BulkConversionException(BulkReasonCode.ConversionFailed, "A page could not be compressed.");
        return data.ToArray();
    }

    private static byte[] EncodeDecoded(byte[] source, int quality)
    {
        using var decoded = SKBitmap.Decode(source)
            ?? throw new BulkConversionException(BulkReasonCode.ConversionFailed, "The image could not be read.");
        using var flat = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var decodedImage = SKImage.FromBitmap(decoded);
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(decodedImage, 0f, 0f, new SKSamplingOptions(), null);
        }
        using var image = SKImage.FromBitmap(flat);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new BulkConversionException(BulkReasonCode.ConversionFailed, "The image could not be compressed.");
        return data.ToArray();
    }

    // The streams and images stay open until the document is saved because PDFsharp reads them lazily.
    private static void AddImagePage(PdfDocument document, byte[] jpeg, double widthPoints, double heightPoints, List<IDisposable> keepAlive)
    {
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(widthPoints);
        page.Height = XUnit.FromPoint(heightPoints);

        var stream = new MemoryStream(jpeg);
        keepAlive.Add(stream);
        var image = XImage.FromStream(stream);
        keepAlive.Add(image);

        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(image, 0, 0, widthPoints, heightPoints);
    }

    private static byte[] Save(PdfDocument document)
    {
        using var buffer = new MemoryStream();
        document.Save(buffer, false);
        return buffer.ToArray();
    }
}
