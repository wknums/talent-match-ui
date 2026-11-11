# Spike S3 — extraction quality on raster PDFs

**Task**: T008 — **status: DONE** (2026-10-06)
**Question**: does the existing extraction path read a raster (image-only) PDF as well as the original, and what DPI/quality fits the size budget?

## Method

- Harness: `dotnet/spikes/S3.ExtractionParity` (not part of `TalentMatch.slnx`; reuses the S2 synthetic PDF writer and `PdfiumRasterizer`).
- **20 synthetic CVs** (no real data), 1 to 11 pages, text-based PDFs. Each CV is generated from known facts: name, e-mail, phone, employers, job titles, periods, eight skills, a measured achievement per bullet, degree, school and a certification. In total **3,719 facts in 11 categories**.
- Three versions of each CV were sent through the same service call the product uses for every document (`POST {AWR_SEQ_API_ENDPOINT}/assess/passthrough`, the extraction prompt from `LlmProxyService.ExtractAsync`): the **original**, a raster at **150 DPI / JPEG 80** (the default) and a raster at **120 DPI / JPEG 70**. 60 calls in total.
- A fact counts as found when its normalized text appears in the extracted Markdown (phone numbers are compared as digits). Recall is the share of facts found.
- The call used a throwaway service principal holding only the API's `TalentMatch.Access` role; it has been deleted.

## Result: field-level parity

Whole set (all 20 CVs):

| Category | Facts | original | 150/80 | 120/70 |
| --- | --- | --- | --- | --- |
| certification | 20 | 90.0% | 90.0% | 90.0% |
| company | 501 | 85.0% | 96.4% | 93.0% |
| degree | 20 | 90.0% | 90.0% | 90.0% |
| email | 20 | 100% | 100% | 95.0% |
| metric | 1,936 | 87.4% | 97.5% | 94.5% |
| name | 20 | 100% | 100% | 95.0% |
| period | 501 | 85.4% | 97.6% | 93.8% |
| phone | 20 | 100% | 100% | 100% |
| school | 20 | 90.0% | 90.0% | 85.0% |
| skill | 160 | 100% | 100% | 100% |
| title | 501 | 85.0% | 96.4% | 93.0% |
| **all** | 3,719 | **87.3%** | **97.3%** | **94.2%** |

The whole-set figures are dominated by the two longest CVs (below), so the comparison that matters is **CVs of up to 8 pages** (18 CVs, 2,622 facts), computed from the per-CV results:

| Version | Facts missed | Recall |
| --- | --- | --- |
| original | about 17 | **about 99.3%** |
| raster 150/80 | about 23 | **about 99.1%** |
| raster 120/70 | about 23 | **about 99.1%** |

- Of the 18 CVs, 15 lose nothing or almost nothing. In 3 two-page CVs the 150/80 raster lost 2 to 4 of about 85 facts (employer/title lines); the original lost none. 120/70 lost 0 to 3 in the same CVs. The misses are scattered single lines, not a category that fails.
- Extraction takes about 18 s per call for the original and 18 to 23 s for a raster; rasterizing does not change the cost materially.

### The longest CVs fail for both versions

| CV | Pages | original | 150/80 | 120/70 |
| --- | --- | --- | --- | --- |
| 19 | 10 | 42.8% | 93.8% | 99.2% |
| 20 | 11 | 73.2% | 92.2% | 66.7% |

For both CVs the **original** lost large parts, and which version did better is unpredictable (the raster won on CV 19, 120/70 lost on CV 20). The extracted text is cut short or condensed. This is a property of the existing extraction path on very long documents, **not of rasterization**, and it applies to bulk and optional uploads alike. It is a finding to pass on, not something this feature changes.

## Converted size

| Pages | CVs | Original KB | 150/80 KB | 150/80 KB per page | 120/70 KB | 120/70 KB per page |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 4 | 3 | 184 | 184 | 121 | 121 |
| 2 | 5 | 6 | 463 | 231 | 302 | 151 |
| 3 | 3 | 10 | 760 | 253 | 495 | 165 |
| 4 | 2 | 14 | 1,027 | 257 | 671 | 168 |
| 5 | 2 | 18 | 1,354 | 271 | 879 | 176 |
| 6 | 1 | 21 | 1,652 | 275 | 1,074 | 179 |
| 8 | 1 | 29 | 2,228 | 278 | 1,441 | 180 |
| 10 | 1 | 37 | 2,838 | 284 | 1,843 | 184 |
| 11 | 1 | 39 | 3,045 | 277 | 1,973 | 179 |

None exceeds the 8 MiB limit. These pages are lighter than the dense pages measured in S2 (about 550 KB per page at 150/80), and 120/70 is about **35% smaller** than 150/80 on both kinds of page.

## Recommendation (feeds the R7 decision)

1. **Keep 150 DPI / JPEG 80 as the default.** Up to 8 pages it reads as well as the original (about 99%), so the option does not need a quality warning in its help text.
2. **Adopt 120 DPI / JPEG 70 as the automatic step-down** for a document whose 150/80 output exceeds the per-file limit (the S2 `converted_too_large` case, roughly 15 or more dense pages). It reads as well as 150/80 on the CVs tested and fits about 1.5 times as many pages. Implementing the fallback is a small follow-up to `PdfiumRasterizer` and is not part of this spike.
3. Do not go below 120/70 without another measurement.

## Limits of this spike

- **Synthetic, text-based CVs.** Real scanned CVs, photographs and unusual layouts were not read here; S2 measured the size of scanned pages, not their readability.
- **Word and Markdown** files (converted by the source and then rasterized) were not part of the 20 CVs.
- **One run per version.** The service is not deterministic, so single-line differences of 1 to 2 facts are within noise. Only the long-CV failures and the 3-of-18 small gaps are worth noting.
- The fact check is a text search; it does not judge structure or formatting.
