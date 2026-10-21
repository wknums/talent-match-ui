using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using S2.PdfiumBenchmark;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure.Rasterization;

// Spike S2 (T007): PDFium throughput, memory and converted-size distribution.
//   dotnet run -c Release --project dotnet/spikes/S2.PdfiumBenchmark [--quick]
// Prints Markdown that can be pasted into specs/001-sharepoint-bulk-scoring/validation/spike-s2.md.

const long LimitBytes = 8L * 1024 * 1024;
var quick = args.Contains("--quick");
// --only 3 runs a single section (1 to 6)
var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault();
bool Run(int section) => only is null || only == section.ToString();
var options = new RasterizationOptions(150, 80, 30, TimeSpan.FromMinutes(2));
var total = quick ? 60 : 500;

Console.WriteLine($"### Environment\n\n- {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}, {(IsOptimized() ? "Release" : "DEBUG build - numbers are not representative")}");
Console.WriteLine($"- Output settings: {options.Dpi} DPI, JPEG quality {options.JpegQuality}; size limit {LimitBytes / 1024 / 1024} MiB\n");

// ---- inputs: 80% text CVs (two thirds with a photo), 20% scanned CVs; all two pages
var docs = Enumerable.Range(0, total)
    .Select(i => i % 5 == 0 ? SyntheticCv.ScannedCv(2, i) : SyntheticCv.TextCv(2, i, photo: i % 3 != 0))
    .ToList();
Console.WriteLine($"- Inputs: {total} two-page CVs, {docs.Count(d => d.Length > 500_000)} scanned; input size median {Kb(PercentileL(docs.Select(d => (long)d.Length), 50))} KB, max {Kb(docs.Max(d => d.Length))} KB\n");

// ---- 1. service time per document, one at a time
if (Run(1))
using (var single = new PdfiumRasterizer(new BulkScoringOptions { ConvertConcurrency = 1 }))
{
    await single.RasterizeAsync(docs[1], "application/pdf", options); // warm-up (native library load)
    var times = new List<double>();
    foreach (var doc in docs.Take(quick ? 10 : 30))
    {
        var sw = Stopwatch.StartNew();
        await single.RasterizeAsync(doc, "application/pdf", options);
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine($"### 1. Service time, one document at a time ({times.Count} documents)\n");
    Console.WriteLine($"| p50 | p95 | max | per page (p50) |\n| --- | --- | --- | --- |\n| {Percentile(times, 50):F0} ms | {Percentile(times, 95):F0} ms | {times.Max():F0} ms | {Percentile(times, 50) / 2:F0} ms |\n");
}

// ---- 2. does throughput scale with concurrency? (PDFium access may be serialised)
Console.WriteLine("### 2. Throughput by concurrency\n\n| Concurrency | Documents | Elapsed | Documents per minute |\n| --- | --- | --- | --- |");
foreach (var concurrency in Run(2) ? new[] { 1, 2, 4 } : [])
{
    using var rasterizer = new PdfiumRasterizer(new BulkScoringOptions { ConvertConcurrency = concurrency });
    var batch = docs.Take(quick ? 24 : 100).ToList();
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(batch.Select(d => rasterizer.RasterizeAsync(d, "application/pdf", options)));
    Console.WriteLine($"| {concurrency} | {batch.Count} | {sw.Elapsed.TotalSeconds:F1} s | {batch.Count / sw.Elapsed.TotalMinutes:F0} |");
}
Console.WriteLine();

// ---- 3. the acceptance run: all documents at concurrency 2, with memory sampling
if (Run(3))
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var baseline = WorkingSetMiB();
    using var rasterizer = new PdfiumRasterizer(new BulkScoringOptions { ConvertConcurrency = 2 });
    using var sampler = new MemorySampler();
    var sizes = new List<long>();
    var failures = new List<string>();
    var afterFirstFifth = 0d;
    var done = 0;
    var sw = Stopwatch.StartNew();

    await Task.WhenAll(docs.Select(async doc =>
    {
        try
        {
            var result = await rasterizer.RasterizeAsync(doc, "application/pdf", options);
            lock (sizes) sizes.Add(result.PdfBytes.LongLength);
        }
        catch (Exception ex)
        {
            lock (failures) failures.Add(ex.GetType().Name + ": " + ex.Message);
        }
        if (Interlocked.Increment(ref done) == total / 5) afterFirstFifth = WorkingSetMiB();
    }));
    sw.Stop();

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var final = WorkingSetMiB();
    var over = sizes.Count(s => s > LimitBytes);

    Console.WriteLine($"### 3. Acceptance run: {total} two-page CVs at concurrency 2\n");
    Console.WriteLine("| Measure | Result | Target |\n| --- | --- | --- |");
    Console.WriteLine($"| Total time | {sw.Elapsed.TotalSeconds:F1} s ({sw.Elapsed.TotalMinutes:F2} min) | <= 30 min |");
    Console.WriteLine($"| Throughput | {total / sw.Elapsed.TotalMinutes:F0} documents/min | |");
    Console.WriteLine($"| Failures | {failures.Count} | 0 |");
    Console.WriteLine($"| Working set: start / after 20% / peak / end after GC | {baseline:F0} / {afterFirstFifth:F0} / {sampler.PeakMiB:F0} / {final:F0} MiB | growth <= 1024 MiB |");
    Console.WriteLine($"| Memory growth (peak - start) | {sampler.PeakMiB - baseline:F0} MiB | <= 1024 MiB |");
    Console.WriteLine($"| Memory growth (end after GC - start) | {final - baseline:F0} MiB | no leak |");
    Console.WriteLine($"| Converted size p50 / p95 / max | {Kb(PercentileL(sizes, 50))} / {Kb(PercentileL(sizes, 95))} / {Kb(sizes.Max())} KB | |");
    Console.WriteLine($"| Outputs over {LimitBytes / 1024 / 1024} MiB | {over} of {sizes.Count} ({100d * over / Math.Max(1, sizes.Count):F1}%) | recorded |\n");
    foreach (var group in failures.GroupBy(f => f).Take(5)) Console.WriteLine($"- failure x{group.Count()}: {group.Key}");
}

// ---- 4. converted size by page count (also feeds spike S3)
if (Run(4))
{
    Console.WriteLine("### 4. Converted size by page count\n");
    Console.WriteLine("| Pages | Type | Documents | Output p50 | Output max | KB per page (p50) | Over 8 MiB |\n| --- | --- | --- | --- | --- | --- | --- |");
    using var rasterizer = new PdfiumRasterizer(new BulkScoringOptions { ConvertConcurrency = 2 });
    foreach (var pages in new[] { 1, 2, 3, 5, 10, 20, 30 })
    foreach (var scanned in new[] { false, true })
    {
        var samples = Enumerable.Range(0, quick ? 2 : 5)
            .Select(i => scanned ? SyntheticCv.ScannedCv(pages, 1000 + i) : SyntheticCv.TextCv(pages, 1000 + i, photo: true)).ToList();
        var results = await Task.WhenAll(samples.Select(d => rasterizer.RasterizeAsync(d, "application/pdf", options)));
        var bytes = results.Select(r => r.PdfBytes.LongLength).ToList();
        Console.WriteLine($"| {pages} | {(scanned ? "scanned" : "text + photo")} | {bytes.Count} | {Kb(PercentileL(bytes, 50))} KB | {Kb(bytes.Max())} KB | {Kb(PercentileL(bytes, 50)) / pages} | {bytes.Count(b => b > LimitBytes)} |");
    }
}

// ---- 5. how much do DPI and JPEG quality buy? (the fallback if long documents exceed the size limit)
if (Run(5))
{
    Console.WriteLine("\n### 5. Size sensitivity to DPI and JPEG quality\n");
    Console.WriteLine("| DPI | JPEG quality | 2-page text (KB) | 10-page text (KB) | 10-page scanned (KB) | Text KB per page | Pages of dense text before 8 MiB |\n| --- | --- | --- | --- | --- | --- | --- |");
    using var rasterizer = new PdfiumRasterizer(new BulkScoringOptions { ConvertConcurrency = 2 });
    var two = SyntheticCv.TextCv(2, 7, photo: true);
    var ten = SyntheticCv.TextCv(10, 7, photo: true);
    var tenScanned = SyntheticCv.ScannedCv(10, 7);
    foreach (var (dpi, quality) in new[] { (150, 80), (150, 70), (150, 60), (120, 80), (120, 70), (100, 70) })
    {
        var o = options with { Dpi = dpi, JpegQuality = quality };
        var a = (await rasterizer.RasterizeAsync(two, "application/pdf", o)).PdfBytes.LongLength;
        var b = (await rasterizer.RasterizeAsync(ten, "application/pdf", o)).PdfBytes.LongLength;
        var c = (await rasterizer.RasterizeAsync(tenScanned, "application/pdf", o)).PdfBytes.LongLength;
        Console.WriteLine($"| {dpi} | {quality} | {Kb(a):F0} | {Kb(b):F0} | {Kb(c):F0} | {Kb(b) / 10:F0} | {LimitBytes / (b / 10d):F0} |");
    }
}

// ---- 6. colour fringes: does PDFium's default text rendering add chroma that inflates the JPEG?
if (Run(6))
{
    Console.WriteLine("\n### 6. PDFium render flags on a dense text page (150 DPI, JPEG quality 80)\n");
    Console.WriteLine("| Flags | Render ms | Pixels with colour fringes | JPEG KB |\n| --- | --- | --- | --- |");
    var text = SyntheticCv.TextCv(1, 11, photo: false);
    using var reader = Docnet.Core.DocLib.Instance.GetDocReader(text, new Docnet.Core.Models.PageDimensions(150 / 72d));
    using var page = reader.GetPageReader(0);
    var variants = new List<(string Name, Docnet.Core.Models.RenderFlags Flags)> { ("default (RenderAnnotations)", Docnet.Core.Models.RenderFlags.RenderAnnotations) };
    foreach (var flag in Enum.GetValues<Docnet.Core.Models.RenderFlags>())
        if (flag != Docnet.Core.Models.RenderFlags.RenderAnnotations && (int)flag != 0)
            variants.Add(($"RenderAnnotations | {flag}", Docnet.Core.Models.RenderFlags.RenderAnnotations | flag));
    foreach (var (name, flags) in variants)
    {
        var sw = Stopwatch.StartNew();
        var bgra = page.GetImage(flags);
        var elapsed = sw.Elapsed.TotalMilliseconds;
        var colored = 0;
        for (var p = 0; p + 3 < bgra.Length; p += 4)
            if (Math.Abs(bgra[p] - bgra[p + 1]) > 24 || Math.Abs(bgra[p + 1] - bgra[p + 2]) > 24) colored++;
        for (var p = 0; p + 3 < bgra.Length; p += 4)
        {
            var alpha = bgra[p + 3];
            if (alpha == 255) continue;
            var inv = 255 - alpha;
            bgra[p] = (byte)((bgra[p] * alpha + 255 * inv + 127) / 255);
            bgra[p + 1] = (byte)((bgra[p + 1] * alpha + 255 * inv + 127) / 255);
            bgra[p + 2] = (byte)((bgra[p + 2] * alpha + 255 * inv + 127) / 255);
            bgra[p + 3] = 255;
        }
        using var image = SkiaSharp.SKImage.FromPixelCopy(new SkiaSharp.SKImageInfo(page.GetPageWidth(), page.GetPageHeight(), SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque), bgra);
        var jpegBytes = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80).Size;
        Console.WriteLine($"| {name} | {elapsed:F0} | {colored:N0} | {Kb(jpegBytes):F0} |");
    }
}

static bool IsOptimized() =>
    !(typeof(Program).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false).FirstOrDefault() is DebuggableAttribute { IsJITOptimizerDisabled: true });

static double WorkingSetMiB()
{
    using var process = Process.GetCurrentProcess();
    return process.WorkingSet64 / 1024d / 1024d;
}


static double Kb(double bytes) => bytes / 1024;

static double Percentile(IEnumerable<double> values, double percentile)
{
    var sorted = values.OrderBy(v => v).ToList();
    return sorted[Math.Clamp((int)Math.Ceiling(percentile / 100d * sorted.Count) - 1, 0, sorted.Count - 1)];
}

static long PercentileL(IEnumerable<long> values, double percentile) => (long)Percentile(values.Select(v => (double)v), percentile);

sealed class MemorySampler : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private double _peak;

    public MemorySampler() => _loop = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                using var process = Process.GetCurrentProcess();
                var current = process.WorkingSet64 / 1024d / 1024d;
                if (current > _peak) _peak = current;
            }
        }
        catch (OperationCanceledException) { }
    });

    public double PeakMiB => _peak;

    public void Dispose()
    {
        _stop.Cancel();
        _loop.Wait();
        _stop.Dispose();
    }
}


