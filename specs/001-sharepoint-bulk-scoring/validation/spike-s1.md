# Spike S1 — `format=pdf` conversion with app-only `Sites.Selected` (read)

**Task**: T005 — **status: PASS** (DOCX and Markdown).
**Date**: 2026-10-05
**Mode**: cross-tenant, app-only (the cross-tenant test tenant; SharePoint only, synthetic data)
**Runner**: `dotnet test dotnet/tests/Infrastructure.Tests --filter "FullyQualifiedName~GraphLiveIntegrationTests"` with `BULK_LIVE_GRAPH=1`; environment loaded from the `.env_*` profile by `infra/scripts/lib/sharepoint-access.sh`.

## Setup under test

| Item | Value |
| --- | --- |
| Credential | Self-signed certificate (no client secret exists on the app) |
| Graph permission on the app | Application `Sites.Selected` only |
| Site grant | Role **`read`** on one site (the tenant's root site) |
| Call | `GET /drives/{drive-id}/items/{item-id}/content?format=pdf` through `GraphSharePointSource.ConvertToPdfAsync`, 32 MB cap |

## Results

| Input | Result | Evidence |
| --- | --- | --- |
| Word (`.docx`) ×1 | **PASS** | Graph returned **HTTP 302**; the redirect target returned **HTTP 200, `application/pdf`, 39,363 bytes**; the live test confirmed the content is a valid PDF |
| Markdown (`.md`) ×1 (first of 67 in the folder) | **PASS** | HTTP 302 → **HTTP 200, `application/pdf`, 28,720 bytes**; valid PDF per the live test |

- The test now converts **one file of each convertible type** found in the folder and prints which types it found, so a missing type is reported instead of silently skipped.
- Permission used: **`Sites.Selected`, site role `read`** only. The `write` fallback (research.md R7 option a) was **not needed**, and `SHAREPOINT_ALLOW_WRITE` was never set.
- The 302 redirect points to a pre-authenticated download URL; the product follows it without the bearer header (the live test covers this path end to end).
- Same-tenant **delegated** conversion remains to be confirmed at the first same-tenant environment that has SharePoint (covered by the mock until then), as the task states.

## Decision

DOCX and Markdown convert to PDF under the read-only role. **No broader permission is required and no fallback is chosen.** T040 (conversion slice), T044 (DOCX/MD path) and T087 are unblocked for the cross-tenant path.

## Reproduce

```bash
export ACTIVE_ENV_FILE=".env_qa_mcaps"; source infra/scripts/lib/sharepoint-access.sh; sp_load_env
dotnet test dotnet/tests/Infrastructure.Tests --filter "FullyQualifiedName~Spike_S1" --logger "console;verbosity=detailed"
```
