# v3.1 contract freeze (T010)

**Recorded**: 2026-10-13  
**Constitution**: 3.0.0  
**Contract**: `contracts/openapi.yaml`, OpenAPI 3.1.0  
**Runtime status**: planned target; this record does not claim connector activation, a live
delegated journey, publisher verification, B-GATE, parity, or release readiness.

## Frozen architecture

The only approved source-access architecture is a separate, confidential, multi-tenant Microsoft
365 connector using authorization code, PKCE S256, opaque one-time state, `/organizations`,
delegated read scopes, and per-journey/per-request encrypted MSAL cache partitions. Product identity
continues to authorize product jobs. No application-only reader, `Sites.Selected`, source
host/root/site/drive/folder allow-list, same/cross-tenant mode, OBO source cache, or fallback
identity is part of the target contract.

The v3.1 `x-endpoint-map` freezes the public operations for:

1. connection and consent;
2. discovery, stable review, and confirmation;
3. monitoring, cancellation, retry, and terminal notification; and
4. retained content and administrator settings.

The `x-immutable-audit-contract` freezes all 56 control-plane, request, item, content, cleanup, and
reconciliation event literals plus actor, correlation, UTC timestamp, atomicity, immutability,
safe-payload, and four-way provider/stack parity rules.

## Current setup evidence

| Task | Evidence | Result |
| --- | --- | --- |
| T001 | Exact five-file Stack A baseline under isolated Node 20.19.5 | PASS: 5 files, 28 tests |
| T002 | Exact filtered Stack B baseline under .NET SDK 10.0.401 | PASS: 24 Application + 16 Web tests |
| T003 | Delegated fixture generator and JSON validation | PASS: 2,500 eligible items; 1,000/1,000/500 steps |
| T004 | Poppler 25.07.0/scikit-image 0.24.0 oracle | PASS: identical fixtures accepted; reordered, blank, and cropped fixtures rejected |
| T005 | Fingerprint/disclosure/problem fixture validation | PASS: 4 raw-byte hashes and all 19 OpenAPI error literals |
| T006 | `BulkScoringOpenApiContractTests` | PASS: 4 tests |
| T007 | TypeScript 5.7 build and bulk DTO legacy-field scan | PASS |
| T008 | v3.1 requirement/endpoint-map regression | PASS through the same 4 OpenAPI tests |
| T009 | Repository policy regression | Intentional RED on stale runtime/infrastructure paths |

The T009 red result is expected at contract freeze: the test proves it detects the superseded
fields and deployment paths that later sequenced Stack B removal tasks must eliminate. It is not a
waiver and must be green before the infrastructure/security gate.

## Preserved evidence

The following access-model-independent records remain valid only within their stated assumptions:

- `validation/stack-b-baseline.md`
- `validation/spike-s2.md`
- `validation/spike-s3.md`

`validation/live-stack-b.md`, S1/S4, and the OBO/app-only portions of S5 are historical evidence
only. They cannot satisfy any v3.1 live, stack, parity, publisher, or release gate.

## Exact commands already executed

```text
npx -y -p node@20.19.5 node ./node_modules/vitest/vitest.mjs run tests/integration/application-upload-duplicates.test.ts tests/unit/upload-applications-dialog.test.tsx tests/integration/optional-upload-session.test.ts tests/integration/optional-upload-settings.test.ts tests/unit/optional-upload-settings-ui.test.tsx

dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~UploadApplicationsCommandTests|FullyQualifiedName~OptionalUploadSessionTests|FullyQualifiedName~UploadSettingsTests|FullyQualifiedName~OptionalUploadEndpointsTests|FullyQualifiedName~UploadSettingsEndpointsTests|FullyQualifiedName~UploadSettingsPageTests"

dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkScoringOpenApiContractTests"

npx -y -p node@20.19.5 node ./node_modules/vitest/vitest.mjs run tests/security/bulk-source-repository-policy.test.ts
```

The first three commands exited 0. The repository-policy command exited 1 for the intended stale
source-access findings.

## Required red-first continuation

Before implementation begins, T011–T018 must be authored and each owning slice observed failing for
its intended missing behavior. The aggregate foundation commands are:

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~Microsoft365PublisherGateTests"
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkSourceConnection|FullyQualifiedName~BulkRequestAuthorization|FullyQualifiedName~BulkSourceAdmission|FullyQualifiedName~AuthorizationEnvelope|FullyQualifiedName~Microsoft365SourceOptions"
```

These commands are recorded here as required future red-to-green evidence; this setup record does
not claim they have run or passed.

## Operational-document sequence

1. Keep spec, plan, research, data model, OpenAPI, and quickstart explicitly labeled planned.
2. Do not activate Stack B operational guidance until old-work inventory/discard, runtime removal,
   Terraform, blank templates, migration, telemetry, deletion, provider, and security tasks pass.
3. Do not remove or activate Stack A source paths until B-GATE T100 passes.
4. Do not claim either live gate from mock or automated evidence.
5. Switch release/operator language only after the distinct live gates, aggregate stack gates,
   parity gate, publisher/commercial-cloud proof, and release gate pass.

## Constitution result

The post-design constitution evaluation remains **PASS** for the planned design. The pass depends
on implementing and validating the complete Principle I matrix, Clean Architecture boundaries,
storage abstraction, delegated-only source access, protected caches, source-independent
phase-aware lapse behavior, and exact gate order. No implementation or release gate is implied by
this design-time result.
