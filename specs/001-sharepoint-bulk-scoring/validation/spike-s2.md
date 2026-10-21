# Spike S2 — PDFium throughput, stability and converted-size distribution

**Task**: T007 — **status: PASS** (all pass criteria met on two operating systems; one finding about long documents, below).
**Date**: 2026-10-05
**Harness**: `dotnet/spikes/S2.PdfiumBenchmark` (not part of `TalentMatch.slnx`; `dotnet run -c Release --project dotnet/spikes/S2.PdfiumBenchmark`, `--quick` for a short run, `--only <1-6>` for one section).
**Code under test**: the production `PdfiumRasterizer` (`dotnet/src/Infrastructure/Rasterization`), 150 DPI, JPEG quality 80, concurrency from `BULK_CONVERT_CONCURRENCY` (default 2).

## Libraries chosen

| Job | Package | Version |
| --- | --- | --- |
| Render pages | `Docnet.Core` (PDFium) | 2.6.0 |
| JPEG-encode pages, decode PNG/JPEG | `SkiaSharp` + `SkiaSharp.NativeAssets.Linux.NoDependencies` | 4.152.1 |
| Assemble the image-only PDF | `PDFsharp` | 6.2.4 |

All MIT or BSD/Apache. The fallback in research.md (PDFiumCore with explicit locking, or an out-of-process renderer) was **not needed**: throughput scales with concurrency and nothing crashed or hung.

## Method

- **Inputs**: 500 synthetic two-page CVs, generated deterministically as raw PDFs: 80% text CVs (about 55 dense lines of 10 pt text per page, grey section bars, two thirds with an embedded photo on page 1) and 20% scanned CVs (every page is one full-page noisy JPEG). Text is deliberately **denser than a typical CV**, so sizes are a high estimate.
- **Environments**, both on the same laptop (12 logical CPUs): **Windows 11** and **Debian 13 (trixie) under WSL2**, .NET 10.0.12 Release (self-contained linux-x64 publish for Debian). Debian is the closest available stand-in for App Service on Linux; the real App Service runtime has **not** been exercised.
- Timings varied by about ±25% between repeat runs on this shared laptop; read them as order of magnitude.

## Results against the pass criteria

| Criterion | Target | Windows | Debian (WSL2) |
| --- | --- | --- | --- |
| 500 two-page CVs, concurrency 2 | ≤ 30 min | **58 s** (516 documents/min) | **62 s** (486 documents/min) |
| Failures | 0 | **0** | **0** |
| Memory growth (peak working set − start) | ≤ 1 GiB | 584 MiB | 340 MiB |
| Leak (working set after final GC − start) | none | −130 MiB | −387 MiB |
| Share of outputs over 8 MiB (two-page CVs) | recorded | **0 of 500** | **0 of 500** |
| Converted size p50 / p95 / max (two pages) | recorded | 1,113 / 1,156 / 1,168 KB | 1,135 / 1,170 / 1,182 KB |

At concurrency 2 the machine converts about 500 documents a minute, so the 30,000-file maximum would take about an hour.

### Service time and scaling

| Measure | Windows | Debian |
| --- | --- | --- |
| One document at a time: p50 / p95 / max | 208 / 423 / 446 ms | 194 / 262 / 324 ms |
| Per page (p50) | 104 ms | 97 ms |
| Documents per minute at concurrency 1 / 2 / 4 | 245 / 513 / 653 | 375 / 512 / 523 |

Throughput roughly doubles from 1 to 2 workers, so PDFium access is **not** serialised by the wrapper. Going beyond 2 helps on a 12-CPU machine (Windows), but the default of 2 is right for the small App Service plans this feature targets.

### Memory

The peak working set (1,258 MiB Windows, 1,373 MiB Debian) includes the harness holding all 500 inputs and is the garbage collector being lazy with large temporary buffers (each rendered page is an 8 MB bitmap), not a need: a run with a **512 MiB managed-heap limit** (`DOTNET_GCHeapHardLimit=0x20000000`) completed the same 500 documents with **no failures in 57 s** and a peak working set of 566 MiB. There is no leak (the working set falls after GC). On App Service the .NET runtime sizes its heap from the container limit, so no extra setting is required; set `DOTNET_GCHeapHardLimitPercent` only if a small plan shows pressure.

### Converted size by page count (output p50 per page, KB)

| Pages | Text + photo, Windows | Scanned, Windows | Text + photo, Debian | Scanned, Debian | Over 8 MiB |
| --- | --- | --- | --- | --- | --- |
| 1 | 553 | 574 | 563 | 581 | 0 |
| 2 | 549 | 570 | 560 | 578 | 0 |
| 5 | 552 | 572 | 563 | 579 | 0 |
| 10 | 552 | 575 | 563 | 582 | 0 (5.4–5.8 MB) |
| 20 | 553 | 576 | 564 | 583 | **all** (11–11.7 MB) |
| 30 | 554 | 575 | 565 | 582 | **all** (16.6–17.5 MB) |

A dense page costs about **550–580 KB** whether it is text or a scan, so size grows linearly with the page count. At 150 DPI and quality 80 a document of **about 14–15 pages** reaches the 8 MiB limit.

### What would reduce the size (150 DPI / quality 80 is the baseline)

| DPI | JPEG quality | 2-page text (KB) | 10-page text (KB) | Text KB per page | Pages before 8 MiB (dense text) |
| --- | --- | --- | --- | --- | --- |
| 150 | 80 | 1,127 | 5,564 | 556 | 15 |
| 150 | 70 | 962 | 4,753 | 475 | 17 |
| 150 | 60 | 849 | 4,190 | 419 | 20 |
| 120 | 80 | 848 | 4,174 | 417 | 20 |
| 120 | 70 | 722 | 3,558 | 356 | 23 |
| 100 | 70 | 539 | 2,657 | 266 | 31 |

PDFium render flags give nothing: no colour fringes appear in any variant and the JPEG size is identical; turning text anti-aliasing off makes it *larger* (640 KB). The size comes from the amount of sharp edges, not from a rendering setting.

## Findings

1. **Throughput, stability and memory pass on Windows and Linux.** No failures in over 1,500 conversions across the runs; Docnet's `pdfium.so` and SkiaSharp's `libSkiaSharp.so` load and render correctly on Debian 13.
2. **Long documents do not fit.** `BULK_RASTER_MAX_PAGES` defaults to 30, but a 16+ page raster PDF exceeds the 8 MiB limit and the item fails as `converted_too_large` ("The converted document is larger than the allowed size."). Documents up to about 10 pages (every realistic CV) are comfortably inside.
3. **A real bug was found and fixed by the tests**: PDFium returns pages on a **transparent** background, so encoding them directly as opaque JPEGs gave black pages. `PdfiumRasterizer` now composites each page onto white; the regression test is `The_rendered_page_is_not_blank_and_keeps_a_white_background`.
4. PDFsharp stores identical page images only once; irrelevant for real documents, but it hid the size of long scanned documents in my first run (the harness now makes every page distinct).

## Decision

- **Renderer**: Docnet.Core (PDFium) + SkiaSharp + PDFsharp, as planned. No fallback renderer.
- **Defaults stay at 150 DPI, JPEG quality 80, concurrency 2.** No quality or DPI fallback is needed *now*: no output of a document up to 10 pages exceeds the limit.
- **Decided by S3 (2026-10-06, `spike-s3.md`)**: extraction quality holds at 120 DPI / quality 70 (about 99.1% of facts up to 8 pages, same as 150/80). Add the automatic step-down (retry at 120/70 when the 150/80 output exceeds the limit); the defaults stay at 150/80.
- **Not verified**: the real App Service Linux runtime and its memory limit. Run `S2.PdfiumBenchmark --quick` once on a deployed QA host before relying on the throughput numbers there.

## Reproduce

```bash
dotnet run -c Release --project dotnet/spikes/S2.PdfiumBenchmark                 # Windows or any host with the SDK
dotnet publish dotnet/spikes/S2.PdfiumBenchmark -c Release -r linux-x64 --self-contained -o /tmp/s2   # then run /tmp/s2/S2.PdfiumBenchmark on Linux
```
