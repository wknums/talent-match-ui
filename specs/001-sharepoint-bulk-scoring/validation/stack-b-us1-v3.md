# Stack B US1 Validation — v3.1

**Run date:** 2026-10-14  
**Current gate status:** PASS (T057 automated Stack B P1 gate)

## Completed slice

### T029 → T043 strict source-link classification

Red-first command:

```text
dotnet test dotnet/tests/Application.Tests/TalentMatch.Application.Tests.csproj --filter "FullyQualifiedName~SourceLinkClassifierTests"
```

Observed RED: 1 failed, 14 passed. The missing behavior accepted a percent-encoded control
character in the folder path.

After implementation:

```text
PASS: 15 passed, 0 failed
```

The classifier now has no network/token dependency; accepts structurally valid organizational
`*.sharepoint.com` SharePoint and `*-my.sharepoint.com` work/school OneDrive links; rejects
personal, non-commercial, IP, credential, fragment, non-default-port, nested-host, and raw/encoded
control-character inputs before outbound work; normalizes supported OneDrive folder navigation
links; and produces Graph sharing tokens without fetching pasted URLs. The current focused suite
passes **16 tests**, including the selected QA root-site library-navigation URL, which normalizes
the encoded `id` path structurally without a deployment-time host/site/folder allow-list and
without accessing the live URL.

### T039 raster fidelity and post-conversion admission — intentional RED

The current documented per-page SSIM minimum is 0.95. The historical verification below used
stricter 0.98 fixture/oracle assertions; those results are preserved as recorded.

Red-first command run before T054 production implementation:

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRasterFidelityTests"
```

**Observed RED (exit 1):** the solution restored and the new focused test project reached
compilation, then failed on the intended missing post-conversion contract: `RasterizationResult`
does not yet expose configured media type, admitted size/DPI/page/SSIM/validation facts or a safe
rejection result, and `RasterFidelityAdmission` does not yet exist. No live network was used.

After T054 implementation, the same exact command exited 0: **6 passed, 0 failed, 0 skipped**.
The tests use the shared synthetic raster fixtures, verify the pinned Poppler/scikit-image oracle
contract, assert every admitted page has SSIM at least 0.98 at 150 DPI, independently reject
order/dimension, blank, and crop failures, and prove invalid output removal with no original
fallback, application creation, or scoring handoff.

### T037 immutable audit and endpoint contract — intentional RED

Red-first command run before T051 production implementation:

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkAuditContractTests|FullyQualifiedName~SourceConnectionEndpointsTests"
```

**Observed RED (exit 1):** the test projects compile, the frozen 56-row OpenAPI/domain P1 registry
is enumerated, and the new assertions fail for the intended missing production behavior:

- `BulkAuditEvent` is not mapped in either Stack B EF provider, so parity, atomic rollback,
  append-only update/delete rejection, and cleanup-survival assertions cannot pass.
- actor validation does not yet reject an initiating user that replaces or is attached to the
  wrong actor kind.
- source-connection POST currently resolves as `405 Method Not Allowed`, and callback routes
  resolve as `404 Not Found`, rather than the versioned endpoint and sanitized RFC 9457 behavior.
- prohibited safe-fact payload categories are not yet rejected.

The focused compiled run reported 4 intentional failures in `BulkAuditContractTests` (57 matrix
rows/assertions passed) and the source endpoint contract remained red. A later exact-command
verification was blocked by unrelated dirty syntax errors already present in
`Infrastructure/SharePoint/Authorization/RequestAuthorizationStore.cs`; that file was not changed.
Both changed test projects were also compiled with `--no-dependencies` successfully (zero errors),
preserving the red test evidence without implementing T051.

## T057 executable Stack B P1 red-to-green gate

Exact gate command:

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRasterFidelityTests|FullyQualifiedName~ConfirmBulkScoringRequestTests|FullyQualifiedName~BulkAuditContractTests|FullyQualifiedName~SourceConnectionEndpointsTests"
```

Final result: **exit 0; 80 passed, 0 failed, 0 skipped**:

- Application: **8 passed** (`ConfirmBulkScoringRequestTests`);
- Infrastructure: **67 passed** (`BulkRasterFidelityTests` plus the complete P1
  `BulkAuditContractTests` matrix); and
- Web: **5 passed** (`SourceConnectionEndpointsTests`).

The confirmation suite starts one monotonic stopwatch at confirmation and asserts both durable
acknowledgment and the first authorized query of the returned request within the same 60-second
budget. The raster suite proves invalid converted output is quarantined or removed, the original
is never used as fallback, and no application/scoring handoff occurs. The audit suite covers every
applicable expanded P1 event with correlation ID, authenticated user or explicit service actor,
UTC time, atomic rollback or durable external-boundary decision, append-only cleanup survival,
versioned safe facts, prohibited-payload scans, and matching SQLite/SQL Server behavior.

Supporting focused validations also passed for connector authorization, admission ordering,
pending/request protected stores, delegated exact-folder Graph access, bounded discovery, stable
draft review, byte-authoritative settings, retained content versions, raw-byte fingerprint
compatibility, and accessible redirect/resume/review/confirm/abandon UI. All tests used local
fakes/fixtures; no Microsoft login, tenant consent, live SharePoint URL, deployment, or release
gate was attempted.
