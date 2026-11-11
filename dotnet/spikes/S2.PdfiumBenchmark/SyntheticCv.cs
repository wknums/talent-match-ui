using System.Text;
using SkiaSharp;

namespace S2.PdfiumBenchmark;

// Synthetic, deterministic CV PDFs built as raw PDF so no font or PDF library is needed:
//  - text CVs: ~55 lines of 10 pt text per page, section bars, and an optional embedded photo on page 1
//  - scanned CVs: every page is one full-page noisy JPEG (the worst case for the size limit)
// Everything is synthetic; no real candidate data.
internal static class SyntheticCv
{
    private static readonly string[] Words =
    [
        "experienced", "engineer", "delivered", "platform", "services", "migration", "azure", "cloud", "team", "lead",
        "customers", "requirements", "design", "implementation", "testing", "automation", "pipeline", "database",
        "performance", "security", "stakeholders", "roadmap", "mentoring", "agile", "scrum", "architecture", "api",
        "analysis", "reporting", "operations", "support", "certified", "project", "manager", "developer", "analyst",
    ];

    private static readonly byte[][] Photos = Enumerable.Range(0, 6).Select(i => PhotoJpeg(100 + i)).ToArray();
    // Scanned pages are generated on demand so that every page of a long document is distinct.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> Scans = new();

    private static byte[] Scan(int index) => Scans.GetOrAdd(index % 64, i => ScanJpeg(200 + i));

    public static byte[] TextCv(int pages, int seed, bool photo)
    {
        var pdf = new RawPdf();
        var photoId = photo ? pdf.AddImage(Photos[seed % Photos.Length], 360, 440) : 0;
        var rnd = new Random(seed);
        var pageIds = new List<int>();
        for (var p = 0; p < pages; p++)
        {
            var content = new StringBuilder();
            content.Append($"q 0.88 g 40 742 532 26 re f Q\nBT /F1 18 Tf 50 748 Td (Candidate {seed:D5} - page {p + 1}) Tj ET\n");
            for (var bar = 0; bar < 4; bar++) content.Append($"q 0.93 g 40 {600 - bar * 150} 532 16 re f Q\n");
            if (photo && p == 0) content.Append("q 90 0 0 110 472 628 cm /Im1 Do Q\n");
            content.Append("BT /F1 10 Tf 12 TL 50 718 Td\n");
            for (var line = 0; line < 55; line++)
                content.Append('(').Append(string.Join(' ', Enumerable.Range(0, 12).Select(_ => Words[rnd.Next(Words.Length)]))).Append(") Tj T*\n");
            content.Append("ET\n");
            pageIds.Add(pdf.AddPage(612, 792, content.ToString(), photo && p == 0 ? photoId : 0));
        }
        return pdf.Build(pageIds);
    }

    public static byte[] ScannedCv(int pages, int seed)
    {
        var pdf = new RawPdf();
        var pageIds = new List<int>();
        for (var p = 0; p < pages; p++)
        {
            var imageId = pdf.AddImage(Scan(seed + p), 1275, 1650);
            pageIds.Add(pdf.AddPage(612, 792, "q 612 0 0 792 0 0 cm /Im1 Do Q\n", imageId));
        }
        return pdf.Build(pageIds);
    }

    private static byte[] PhotoJpeg(int seed)
    {
        var rnd = new Random(seed);
        using var bitmap = new SKBitmap(360, 440);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(360, 440),
                [new SKColor((byte)rnd.Next(60, 200), 120, 90), new SKColor(40, (byte)rnd.Next(60, 200), 160)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 360, 440, paint);
        }
        AddNoise(bitmap, rnd, 14);
        return Encode(bitmap, 85);
    }

    // A scan is a rendered text page plus sensor noise, so it does not depend on fonts installed on the machine.
    private static byte[] ScanJpeg(int seed)
    {
        var rnd = new Random(seed);
        using var reader = Docnet.Core.DocLib.Instance.GetDocReader(TextCv(1, seed, photo: false), new Docnet.Core.Models.PageDimensions(150 / 72d));
        using var page = reader.GetPageReader(0);
        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        var pixels = page.GetImage();
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            for (var c = 0; c < 3; c++)
            {
                var value = (pixels[i + c] * alpha + 250 * (255 - alpha) + 127) / 255;
                pixels[i + c] = (byte)Math.Clamp(value + rnd.Next(-9, 10), 0, 255);
            }
            pixels[i + 3] = 255;
        }
        using var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), pixels);
        return image.Encode(SKEncodedImageFormat.Jpeg, 75).ToArray();
    }
    // Noise is applied through a managed copy so the result does not depend on the platform's default pixel layout.
    private static void AddNoise(SKBitmap bitmap, Random rnd, int amplitude)
    {
        var pixels = new byte[bitmap.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        for (var i = 0; i < pixels.Length; i++)
            if (i % 4 != 3) pixels[i] = (byte)Math.Clamp(pixels[i] + rnd.Next(-amplitude, amplitude + 1), 0, 255);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    }

    private static byte[] Encode(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
    }
}

// Minimal PDF writer with binary streams.
internal sealed class RawPdf
{
    private readonly List<byte[]> _objects = [[], [], Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")];

    public int AddImage(byte[] jpeg, int width, int height) =>
        Add(Stream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg));

    public int AddPage(int widthPoints, int heightPoints, string content, int imageId)
    {
        var contentId = Add(Stream("", Encoding.Latin1.GetBytes(content)));
        var xobject = imageId > 0 ? $"/XObject << /Im1 {imageId} 0 R >>" : "";
        return Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPoints} {heightPoints}] /Contents {contentId} 0 R /Resources << /Font << /F1 3 0 R >> {xobject} >> >>"));
    }

    public byte[] Build(IReadOnlyList<int> pageIds)
    {
        _objects[0] = Ascii("<< /Type /Catalog /Pages 2 0 R >>");
        _objects[1] = Ascii($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");
        using var output = new MemoryStream();
        Write(output, "%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < _objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write(output, $"{i + 1} 0 obj\n");
            output.Write(_objects[i]);
            Write(output, "\nendobj\n");
        }
        var xref = output.Position;
        Write(output, $"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(output, $"{offset:D10} 00000 n \n");
        Write(output, $"trailer\n<< /Size {_objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private int Add(byte[] body)
    {
        _objects.Add(body);
        return _objects.Count;
    }

    private static byte[] Stream(string dictionary, byte[] data)
    {
        using var stream = new MemoryStream();
        Write(stream, $"<< {dictionary} /Length {data.Length} >>\nstream\n");
        stream.Write(data);
        Write(stream, "\nendstream");
        return stream.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    private static void Write(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));
}

