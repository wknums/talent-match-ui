using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using S3.ExtractionParity;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Infrastructure.Rasterization;

// Spike S3 (T008): extraction on the original PDF vs raster PDFs, scored against the facts written into each CV.
//   S3_TENANT, S3_CLIENT_ID, S3_CLIENT_SECRET   a throwaway client with the AWR API app role (never printed)
//   AWR_SEQ_API_ENDPOINT, AWR_AAD_AUDIENCE      from the environment profile
//   dotnet run -c Release --project dotnet/spikes/S3.ExtractionParity [--dry]
// Extracted text is cached under %TEMP%\s3-spike so a re-run only calls the service for what is missing.

var dry = args.Contains("--dry");
var cache = Path.Combine(Path.GetTempPath(), "s3-spike", "cache");
Directory.CreateDirectory(cache);
var endpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT") ?? throw new InvalidOperationException("AWR_SEQ_API_ENDPOINT");
var audience = Environment.GetEnvironmentVariable("AWR_AAD_AUDIENCE") ?? throw new InvalidOperationException("AWR_AAD_AUDIENCE");
var credential = dry ? null : new ClientSecretCredential(
    Environment.GetEnvironmentVariable("S3_TENANT"), Environment.GetEnvironmentVariable("S3_CLIENT_ID"), Environment.GetEnvironmentVariable("S3_CLIENT_SECRET"));

int[] pageTargets = [1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6, 8, 10, 12];
var docs = pageTargets.Select((p, i) => CvFactory.Create(i + 1, p)).ToList();
var variants = new (string Name, int? Dpi, int? Quality)[] { ("original", null, null), ("150/80", 150, 80), ("120/70", 120, 70) };

Console.WriteLine($"### Inputs\n\n- {docs.Count} synthetic CVs, {docs.Sum(d => d.Facts.Count)} known facts ({docs.SelectMany(d => d.Facts).Select(f => f.Category).Distinct().Count()} categories); pages {docs.Min(d => d.Pages)} to {docs.Max(d => d.Pages)}\n");

// ---- convert
var inputs = new Dictionary<(int Id, string Variant), (byte[] Bytes, int Pages)>();
foreach (var d in docs) inputs[(d.Id, "original")] = (d.Pdf, d.Pages);
using (var rasterizer = new PdfiumRasterizer(new BulkScoringOptions { ConvertConcurrency = 2 }))
foreach (var v in variants.Where(v => v.Dpi is not null))
foreach (var d in docs)
{
    var r = await rasterizer.RasterizeAsync(d.Pdf, "application/pdf", new RasterizationOptions(v.Dpi!.Value, v.Quality!.Value, 30, TimeSpan.FromMinutes(2)));
    inputs[(d.Id, v.Name)] = (r.PdfBytes, r.PageCount);
}

Console.WriteLine("### Converted size by page count\n\n| Pages | CVs | Original KB | 150/80 KB | 150/80 KB/page | 120/70 KB | 120/70 KB/page | 150/80 over 8 MiB | 120/70 over 8 MiB |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
foreach (var g in docs.GroupBy(d => d.Pages).OrderBy(g => g.Key))
{
    double Kb(string v) => g.Average(d => inputs[(d.Id, v)].Bytes.Length) / 1024;
    int Over(string v) => g.Count(d => inputs[(d.Id, v)].Bytes.Length > 8 * 1024 * 1024);
    Console.WriteLine($"| {g.Key} | {g.Count()} | {Kb("original"):F0} | {Kb("150/80"):F0} | {Kb("150/80") / g.Key:F0} | {Kb("120/70"):F0} | {Kb("120/70") / g.Key:F0} | {Over("150/80")} | {Over("120/70")} |");
}
Console.WriteLine();
if (dry) return;

// ---- extract (the service call the product uses for every document: multipart promptFile + specFile)
const string Prompt = """
    You are a document extraction specialist. Extract the text content from the provided document and return it as clean, well-structured Markdown.

    Rules:
    - Preserve headings, lists, and formatting structure
    - Remove headers, footers, page numbers, and decorative elements
    - Normalise whitespace and fix OCR artefacts where obvious
    - Return ONLY the extracted Markdown text, no JSON wrapper

    Output format: raw Markdown text.
    """;

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
var gate = new SemaphoreSlim(3);
var timings = new System.Collections.Concurrent.ConcurrentDictionary<(int, string), double>();
var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

async Task<string?> Extract(int id, string variant)
{
    var path = Path.Combine(cache, $"{id:D2}-{variant.Replace('/', '_')}.md");
    if (File.Exists(path)) return await File.ReadAllTextAsync(path);
    var (bytes, _) = inputs[(id, variant)];
    await gate.WaitAsync();
    try
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var sw = Stopwatch.StartNew();
            var token = (await credential!.GetTokenAsync(new TokenRequestContext([audience]), default)).Token;
            using var form = new MultipartFormDataContent();
            var prompt = new ByteArrayContent(Encoding.UTF8.GetBytes(Prompt));
            prompt.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(prompt, "promptFile", "extraction-prompt.md");
            var doc = new ByteArrayContent(bytes);
            doc.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(doc, "specFile", $"cv-{id:D2}.pdf");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/assess/passthrough") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    timings[(id, variant)] = sw.Elapsed.TotalSeconds;
                    await File.WriteAllTextAsync(path, text);
                    return text;
                }
                failures.Add($"cv {id} {variant} attempt {attempt}: HTTP {(int)response.StatusCode}");
                if ((int)response.StatusCode is 401 or 403 or 400) return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failures.Add($"cv {id} {variant} attempt {attempt}: {ex.GetType().Name}");
            }
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
        return null;
    }
    finally
    {
        gate.Release();
    }
}

var jobs = docs.SelectMany(d => variants.Select(v => (d, v))).ToList();
var results = new Dictionary<(int, string), string?>();
await Task.WhenAll(jobs.Select(async j => { var t = await Extract(j.d.Id, j.v.Name); lock (results) results[(j.d.Id, j.v.Name)] = t; }));
foreach (var f in failures.Take(10)) Console.WriteLine($"- service issue: {f}");

// ---- score
static string Norm(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", " "), "\\s+", " ").Trim();
static bool Found(string category, string fact, string text)
{
    if (category == "phone") return Regex.Replace(text, "\\D", "").Contains(Regex.Replace(fact, "\\D", ""));
    return Norm(text).Contains(Norm(fact));
}

var perVariant = variants.ToDictionary(v => v.Name, v => new Dictionary<string, (int Hit, int Total)>());
var perDoc = new List<(int Id, int Pages, string Variant, double Recall)>();
foreach (var v in variants)
foreach (var d in docs)
{
    var text = results[(d.Id, v.Name)];
    var hits = 0;
    foreach (var (category, fact) in d.Facts)
    {
        var ok = text is not null && Found(category, fact, text);
        if (ok) hits++;
        var m = perVariant[v.Name];
        var cur = m.GetValueOrDefault(category);
        m[category] = (cur.Hit + (ok ? 1 : 0), cur.Total + 1);
    }
    perDoc.Add((d.Id, d.Pages, v.Name, text is null ? double.NaN : (double)hits / d.Facts.Count));
}

Console.WriteLine("\n### Field-level recall (known facts found in the extracted text)\n");
var categories = perVariant["original"].Keys.OrderBy(c => c).ToList();
Console.WriteLine("| Category | Facts | " + string.Join(" | ", variants.Select(v => v.Name)) + " |\n| --- | --- | " + string.Join(" | ", variants.Select(_ => "---")) + " |");
foreach (var c in categories)
    Console.WriteLine($"| {c} | {perVariant["original"][c].Total} | " + string.Join(" | ", variants.Select(v => $"{100d * perVariant[v.Name][c].Hit / perVariant[v.Name][c].Total:F1}%")) + " |");
Console.WriteLine("| **all** | " + perVariant["original"].Values.Sum(x => x.Total) + " | " + string.Join(" | ", variants.Select(v => $"**{100d * perVariant[v.Name].Values.Sum(x => x.Hit) / perVariant[v.Name].Values.Sum(x => x.Total):F1}%**")) + " |");

Console.WriteLine("\n### Recall by page count\n\n| Pages | CVs | " + string.Join(" | ", variants.Select(v => v.Name)) + " |\n| --- | --- | " + string.Join(" | ", variants.Select(_ => "---")) + " |");
foreach (var g in perDoc.GroupBy(x => x.Pages).OrderBy(g => g.Key))
    Console.WriteLine($"| {g.Key} | {g.Select(x => x.Id).Distinct().Count()} | " + string.Join(" | ", variants.Select(v => $"{100 * g.Where(x => x.Variant == v.Name).Average(x => x.Recall):F1}%")) + " |");

var gaps = perDoc.Where(x => x.Variant == "150/80").Join(perDoc.Where(x => x.Variant == "original"), a => a.Id, b => b.Id, (a, b) => (a.Id, a.Pages, Gap: b.Recall - a.Recall)).Where(x => x.Gap > 0.02).ToList();
Console.WriteLine($"\n- CVs where 150/80 recall is more than 2 points below the original: {gaps.Count} of {docs.Count}" + (gaps.Count > 0 ? " (" + string.Join(", ", gaps.Select(g => $"cv {g.Id} ({g.Pages} p, -{100 * g.Gap:F1})")) + ")" : ""));
Console.WriteLine($"- Extraction time per call (median): " + string.Join(", ", variants.Select(v => $"{v.Name} {timings.Where(t => t.Key.Item2 == v.Name).Select(t => t.Value).DefaultIfEmpty(double.NaN).OrderBy(x => x).ElementAt(Math.Max(0, timings.Count(t => t.Key.Item2 == v.Name) / 2)):F1} s")) + " (only calls made in this run)");
Console.WriteLine($"- Missing results: {results.Count(r => r.Value is null)}");

// ---- where do the misses happen? (truncation shows up as every fact after some point missing)
Console.WriteLine("\n### Missing facts, longest CVs and the CVs with a gap\n");
foreach (var d in docs.Where(d => d.Pages >= 8 || gaps.Any(g => g.Id == d.Id)))
foreach (var v in variants)
{
    var text = results[(d.Id, v.Name)] ?? "";
    var missing = d.Facts.Select((f, i) => (f, i)).Where(x => !Found(x.f.Category, x.f.Fact, text)).ToList();
    var firstGap = missing.Count == 0 ? -1 : missing[0].i;
    var tail = missing.Count > 0 && missing.Count == d.Facts.Count - firstGap;
    Console.WriteLine($"- cv {d.Id} ({d.Pages} p) {v.Name}: {text.Length} chars, {missing.Count}/{d.Facts.Count} facts missing" + (missing.Count == 0 ? "" : $", first missing is fact #{firstGap}" + (tail ? " and every later one is missing too (output cut off)" : $" ({string.Join(", ", missing.Take(4).Select(m => m.f.Category + ": " + m.f.Fact))})")));
}
