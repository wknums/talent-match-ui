# Tasks: Dynamic Delegated Microsoft 365 Bulk Scoring

**Input**: Design documents in `C:\code\awr-cv-match-client\specs\001-sharepoint-bulk-scoring\`  
**Authority**: `.specify/memory/constitution.md` version 3.0.0  
**Contract**: `specs/001-sharepoint-bulk-scoring/contracts/openapi.yaml` version 3.1.0  
**Implementation order**: Stack B first. Stack A removal and runtime work MUST NOT start before
B-GATE T100 passes.  
**Tests**: Required and red-first. Every foundation, provider, migration, security, stack-specific,
and cross-stack parity implementation slice must first have a runnable test observed failing for
the intended missing behavior. Cross-stack failures are repaired only after the failing parity
evidence is recorded and block parity and release until green. The complete Principle I matrix
means all preserved control-plane rows plus every request/item/content lifecycle row in OpenAPI;
testing only the former control-plane subset is a failure.  
**Path convention**: All paths are relative to `C:\code\awr-cv-match-client\`.

## Preserved Work and Status Rules

Preserve access-model-independent evidence in
`specs/001-sharepoint-bulk-scoring/validation/stack-b-baseline.md`,
`specs/001-sharepoint-bulk-scoring/validation/spike-s2.md`, and
`specs/001-sharepoint-bulk-scoring/validation/spike-s3.md`. Existing durable processing,
rasterization, fingerprinting, lease recovery, status, and cancellation code may be retained only
after the new tests prove it cannot select or fall back to an old identity mode. App-only,
`Sites.Selected`, certificate-reader, same/cross-tenant, allow-list, and OBO validation records are
historical only and cannot satisfy a current gate. No task is pre-completed.

## Phase 1: Setup — Baselines, Shared Fixtures, and Contract Freeze

**Purpose**: Freeze reproducible inputs and current legacy behavior before runtime changes.

- [X] T001 Rerun the exact Stack A legacy upload/settings baseline on Node 20.19+ and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-a-baseline.md`; validation: `npx vitest run tests/integration/application-upload-duplicates.test.ts tests/unit/upload-applications-dialog.test.tsx tests/integration/optional-upload-session.test.ts tests/integration/optional-upload-settings.test.ts tests/unit/optional-upload-settings-ui.test.tsx` exits 0.
- [X] T002 Rerun the exact Stack B legacy upload/settings baseline and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-b-baseline-v3.md`; validation: `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~UploadApplicationsCommandTests|FullyQualifiedName~OptionalUploadSessionTests|FullyQualifiedName~UploadSettingsTests|FullyQualifiedName~OptionalUploadEndpointsTests|FullyQualifiedName~UploadSettingsEndpointsTests|FullyQualifiedName~UploadSettingsPageTests"` exits 0.
- [X] T003 [P] Extend delegated fixtures for SharePoint, work/school OneDrive, another organizational tenant, commercial/non-commercial accounts, 2,500 files, inaccessible descendants, duplicates, and exact count/byte limits in `tests/fixtures/bulk-scoring-requests/contract-fixtures.json`, `graph-tree.json`, and `generate-files.mjs`.
- [X] T004 [P] Add deterministic asymmetric one-page/multi-page, portrait/landscape/custom-size, edge-content, near-blank, reordered, blank, and cropped raster fixtures plus a version-pinned Poppler/scikit-image oracle in `tests/fixtures/bulk-scoring-requests/raster/`, `tests/tools/raster_fidelity_oracle.py`, and `tests/tools/requirements-raster-fidelity.txt`; validation: the oracle emits per-page order, point dimensions, DPI, SSIM, ink ratio, bounding box, tool versions, and verdict JSON.
- [X] T005 [P] Add raw-byte fingerprint and disclosure/error fixtures covering binary, multibyte, empty, large, compatible pre-change provider values, every OpenAPI error, and every allowed/forbidden surface in `tests/fixtures/bulk-scoring-requests/fingerprint-matrix.json`, `disclosure-matrix.json`, and `problem-cases.json`.
- [X] T006 [P] Add an OpenAPI 3.1.0 regression test for required fields, pagination, limit/configuration versions, FR-010B/FR-010G/FR-019/FR-020 shapes, SC-002 timing semantics, and the complete Principle I audit event map in `dotnet/tests/Web.Tests/BulkScoringOpenApiContractTests.cs`; the test MUST enumerate preserved control-plane rows plus discovered item, claim/lease/import, source-metadata outcomes, content stage/fingerprint, duplicate claim/skip, conversion lifecycle/quarantine/removal, application/document, scoring handoff/start ownership, every item terminal state, retry item, count reconciliation, terminal finalization, content deletion, and cleanup/reconciliation corrections, and MUST reject missing actor type/ID, correlation, UTC time, atomicity, immutability, Stack/provider parity, secret/raw-URL/name/fingerprint/content leakage, or legacy authorization fields.
- [X] T007 [P] Align canonical TypeScript DTOs/enums with OpenAPI 3.1.0 in `src/types/index.ts`, including detail-only actor provenance whose display name is resolved only by the recorded product actor ID and falls back only to that recorded ID, structural prohibition of username fallback, and a notification DTO with no item-name field.
- [X] T008 [P] Keep target artifacts planned and update the v3.1 requirement/endpoint map in `specs/001-sharepoint-bulk-scoring/spec.md`, `plan.md`, `research.md`, `data-model.md`, `quickstart.md`, and `contracts/openapi.yaml`.
- [X] T009 Add repository assertions rejecting source secrets, concrete environment identifiers, old access-mode literals, Graph application roles, and source allow-lists outside dated migration history in `tests/security/bulk-source-repository-policy.test.ts`.
- [X] T010 Record the frozen contract, preserved evidence, red commands, operational-document sequence, and constitution result in `specs/001-sharepoint-bulk-scoring/validation/contract-freeze-v3.md` (depends on T001–T009).

**SETUP-GATE**: T001–T010.

---

## Phase 2: Foundational — Stack B Domain, Schema, Admission, and Protected Authorization

**Purpose**: Build the shared Stack B security and persistence boundary before any story.

### Tests first

- [X] T011 [P] Add failing pending-connection domain tests for actor/job/URL binding, one-time state, idle/absolute expiry, consume/abandon, and cryptographic clearing in `dotnet/tests/Domain.Tests/BulkScoringRequests/BulkSourceConnectionTests.cs` (depends on T010).
- [X] T012 [P] Add failing request-authorization tests for full bindings, lifecycle transitions, retry non-reuse, and removal of `AccessModel`/`OwningStack` in `dotnet/tests/Domain.Tests/BulkScoringRequests/BulkRequestAuthorizationTests.cs` (depends on T010).
- [X] T013 [P] Add failing SQLite/SQL Server model and migration tests for all seven feature tables, checks/indexes, completed-history compatibility, old-active-work refusal, and detached append-only audit-ledger retention/provider parity in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkSourceConnectionMigrationTests.cs` (depends on T010).
- [X] T014 [P] Add failing repository tests for optimistic concurrency, full binding lookup, expiry, consume-once, atomic transfer, and rollback in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkSourceConnectionRepositoryTests.cs` (depends on T010).
- [X] T015 [P] Add failing distributed-admission tests for transactional 10/10/30/60 limits, HMAC-only subjects, cleanup, and cross-instance decisions in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkSourceAdmissionRepositoryTests.cs` (depends on T010).
- [X] T016 [P] Add failing envelope tests for associated-data mismatch, tamper detection, fresh-key rotation, and discard in `dotnet/tests/Infrastructure.Tests/SharePoint/AuthorizationEnvelopeTests.cs` (depends on T010).
- [X] T017 [P] Add failing commercial-cloud, delegated-scope, callback, TTL, admission, file/in-flight, file-count, and aggregate-limit option tests in `dotnet/tests/Application.Tests/BulkScoringRequests/Microsoft365SourceOptionsTests.cs` (depends on T010).
- [X] T018 [P] Add failing Stack B publisher-gate tests for Production true/false/absent, controlled Development/QA false/absent, unknown environments, and a static no-source-tenant/account/host/site/drive/folder signature assertion in `dotnet/tests/Application.Tests/BulkScoringRequests/Microsoft365PublisherGateTests.cs` (depends on T010).

### Implementation

- [X] T019 [P] Implement pending connection/authorization entities, enums, bindings, expiry, consume, abandon, and discard invariants in `dotnet/src/Domain/Entities/BulkScoringRequests/BulkSourceConnection.cs`, `BulkRequestAuthorization.cs`, and `Enums.cs` (depends on T011–T012).
- [X] T020 [P] Revise request, item, settings, content, and typed immutable-audit event contracts in `dotnet/src/Domain/Entities/BulkScoringRequests/` for frozen limits, review/configuration versions, source-stage boundary, FR-019 recorded-actor-ID-only provenance with no username fallback, FR-020 terminal snapshot/acknowledgment, FR-021 retention, and the complete OpenAPI lifecycle event registry; require correlation ID, `user` actor for authenticated actions or explicit `service` actor for worker/timer/cleanup/reconciliation actions, optional initiating user, UTC timestamp, same-transaction or durable outbox/reconciliation boundary, append-only survival, versioned safe payload, prohibited-payload fields, and provider-parity fields (depends on T006, T012, and T017).
- [X] T021 Implement EF mappings and the v3.1 migration for the seven tables, old-active-work refusal, compatible history, actor provenance, and terminal snapshot fields in `dotnet/src/Infrastructure/Persistence/BulkScoringModelConfiguration.cs` and `dotnet/src/Infrastructure/Persistence/Migrations/20261013_DelegatedBulkSourceV3.cs` (depends on T013, T019, and T020).
- [X] T022 [P] Implement connection/request authorization repositories and atomic transfer operations in `dotnet/src/Domain/Interfaces/IBulkSourceConnectionRepository.cs`, `IBulkRequestAuthorizationRepository.cs`, and `dotnet/src/Infrastructure/Persistence/Repositories/BulkSourceConnectionRepository.cs` (depends on T014 and T021).
- [X] T023 [P] Implement transactional HMAC admission storage and cleanup in `dotnet/src/Application/BulkScoringRequests/Services/BulkSourceAdmissionService.cs` and `dotnet/src/Infrastructure/Persistence/Repositories/BulkSourceAdmissionRepository.cs` (depends on T015 and T021).
- [X] T024 [P] Generalize envelope encryption and Key Vault wrapping for pending/request associated data in `dotnet/src/Infrastructure/SharePoint/Authorization/AuthorizationEnvelope.cs` and `KeyVaultAuthorizationKeyWrapper.cs` (depends on T016).
- [X] T025 Implement validated commercial-cloud startup options and fail-closed registration in `dotnet/src/Application/BulkScoringRequests/BulkScoringOptions.cs`, `BulkRequestLimits.cs`, and `dotnet/src/Infrastructure/SharePoint/BulkScoringServiceCollectionExtensions.cs` (depends on T017).
- [X] T026 Implement the Stack B publisher activation gate as a pure deployment-environment/explicit-assertion decision before endpoint/worker registration in `dotnet/src/Application/BulkScoringRequests/Microsoft365PublisherGate.cs` and `dotnet/src/Infrastructure/SharePoint/BulkScoringServiceCollectionExtensions.cs` (depends on T018).
- [X] T027 Implement old-work inventory/drain/discard refusal without credential translation in `dotnet/src/Web.Server/Operations/BulkSourceMigrationPreflight.cs` (depends on T021–T024).
- [X] T028 Run FOUNDATION-GATE and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-b-foundation-v3.md` (depends on T011–T027); validation MUST run `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~Microsoft365PublisherGateTests"` and `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkSourceConnection|FullyQualifiedName~BulkRequestAuthorization|FullyQualifiedName~BulkSourceAdmission|FullyQualifiedName~AuthorizationEnvelope|FullyQualifiedName~Microsoft365SourceOptions"` and require both to exit 0.

**FOUNDATION-GATE**: T028 depends explicitly on T018 and T026, executes
`Microsoft365PublisherGateTests`, depends on SETUP-GATE, and blocks every Stack B story.

---

## Phase 3: Stack B — User Story 1: Connect, Review, and Confirm (Priority: P1) 🎯 MVP

**Goal**: Connect any accessible commercial-cloud organizational SharePoint or work/school OneDrive
folder, review a stable draft, and atomically confirm delegated background work.

**Independent Test**: Complete connect → callback → exact-folder verification → bounded discovery →
stable paginated review → confirm for SharePoint, work OneDrive, and another organizational tenant;
reject personal/non-commercial sources and admission excess before token or Graph work.

### Tests first

- [X] T029 [P] [US1] Add failing structural URL/commercial-cloud tests with zero outbound work on rejection in `dotnet/tests/Application.Tests/BulkScoringRequests/SourceLinkClassifierTests.cs` (depends on T028).
- [X] T030 [P] [US1] Add failing `/organizations` authorization-code, PKCE S256, opaque state, callback replay/expiry, user/admin consent, and product/source identity tests in `dotnet/tests/Infrastructure.Tests/SharePoint/SourceConnectorAuthorizationTests.cs` (depends on T028).
- [X] T031 [P] [US1] Add failing admission-order tests for pending/actor/IP/callback limits and no rejected external work in `dotnet/tests/Application.Tests/BulkScoringRequests/SourceConnectionAdmissionTests.cs` (depends on T028).
- [X] T032 [P] [US1] Add failing encrypted pending-cache binding, expiry, touch, abandon, and callback-failure clearing tests in `dotnet/tests/Infrastructure.Tests/SharePoint/PendingSourceAuthorizationStoreTests.cs` (depends on T028).
- [X] T033 [P] [US1] Add failing delegated-only `/shares`, exact-folder lineage, SharePoint/work-OneDrive, redirect suppression, and inaccessible-descendant tests in `dotnet/tests/Infrastructure.Tests/SharePoint/GraphSharePointSourceTests.cs` (depends on T028).
- [X] T034 [P] [US1] Add failing discovery boundary tests for 1,000-step scanning, exact/one-over per-file/count/aggregate limits, empty/unsupported entries, and stop-before-exceed in `dotnet/tests/Application.Tests/BulkScoringRequests/DiscoverBulkScoringRequestTests.cs` (depends on T028).
- [X] T035 [P] [US1] Add failing stable draft-review tests over 1/30,000/100,000 items with zero Graph/discovery mutation in `dotnet/tests/Application.Tests/BulkScoringRequests/DraftItemReviewTests.cs` (depends on T028).
- [X] T036 [P] [US1] Add failing confirmation tests for fresh access, frozen options, configuration capture, request/item durability, cache transfer, consume-once, recorded submit actor ID/UTC time persistence, rollback, and SC-002 measurement of both confirmation acknowledgment and the first successful authorized query of the returned request within one 60-second monotonic stopwatch in `dotnet/tests/Application.Tests/BulkScoringRequests/ConfirmBulkScoringRequestTests.cs` (depends on T028).
- [X] T037 [P] [US1] Add and run failing Stack B endpoint/problem/disclosure and immutable-audit tests in `dotnet/tests/Web.Tests/SourceConnectionEndpointsTests.cs`, `dotnet/tests/Web.Tests/BulkScoringRequestsEndpointsTests.cs`, `dotnet/tests/Web.Tests/BulkSourceDisclosureMatrixTests.cs`, and `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkAuditContractTests.cs`; enumerate every OpenAPI matrix row and require each applicable P1 row, including discovered item, claim/import, metadata recheck, original/converted stage, fingerprint record, duplicate claim/skip, conversion start/complete/fail, quarantine/removal, application/document creation, scoring handoff, and item terminal rows; prove same-transaction rollback or durable outbox/reconciliation atomicity, correlation ID, user or explicit service actor, optional initiating user, UTC timestamp, update/delete rejection, cleanup survival, prohibited-payload scans, versioned safe facts, and identical SQLite/SQL Server semantics; record the intentional red command `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkAuditContractTests|FullyQualifiedName~SourceConnectionEndpointsTests"` in `specs/001-sharepoint-bulk-scoring/validation/stack-b-us1-v3.md` before implementation (depends on T028).
- [X] T038 [P] [US1] Add failing byte-authoritative Stack B settings and MiB UI tests in `dotnet/tests/Web.Tests/BulkScoringSettingsEndpointsTests.cs` and `UploadSettingsPageTests.cs` (depends on T028).
- [X] T039 [P] [US1] Add and run failing FR-010G plus FR-010B/SC-030 Stack B tests in `dotnet/tests/Infrastructure.Tests/Rasterization/BulkRasterFidelityTests.cs` proving configured output type, PDF magic/signature, structural document validity, positive pages, configured size, exact page count/order, source/output width and height within ±1 point, every page pair SSIM >=0.95 at conversion DPI, independent blank/crop rejection, quarantine or removal of invalid output, no original fallback, and zero application/scoring handoff; record the intentional red command `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRasterFidelityTests"` in `specs/001-sharepoint-bulk-scoring/validation/stack-b-us1-v3.md` before implementation (depends on T004 and T028).
- [X] T040 [P] [US1] Add failing FR-010C Stack B tests for exact retained original/converted content, converted scored/default view, per-call authorized original download, page-count equality, TXT bypass, and no fallback in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkContentVersionTests.cs` and `dotnet/tests/Web.Tests/BulkScoringContentEndpointsTests.cs` (depends on T028).
- [X] T041 [P] [US1] Add failing FR-010H Stack B tests across legacy, optional, and bulk paths plus compatible SQLite/SQL Server records in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkFingerprintCompatibilityTests.cs` (depends on T005 and T028).
- [X] T042 [P] [US1] Add failing bUnit redirect/resume, consent, review/discover-more, limit/configuration, accessibility, and abandon tests in `dotnet/tests/Web.Tests/BulkScoringSourceConnectionUxTests.cs` (depends on T028).

### Implementation

- [X] T043 [US1] Implement strict work/school URL parsing, sharing-token encoding, and personal/non-commercial rejection before Graph in `dotnet/src/Application/BulkScoringRequests/Services/SourceLinkClassifier.cs`, `SourceFolderUrl.cs`, and `CommercialCloudClassifier.cs` (depends on T029).
- [X] T044 [US1] Implement connector authorization, state/PKCE, managed-identity client assertion, one-time redemption, tenant/account/cloud validation, and safe errors in `dotnet/src/Infrastructure/SharePoint/Authorization/Microsoft365SourceConnector.cs`, `SourceCallbackProcessor.cs`, and `ManagedIdentityClientAssertion.cs` (depends on T030 and T043).
- [X] T045 [US1] Enforce product/job authorization, shape, pending quota, actor/IP starts, and callback throttle before external work in `dotnet/src/Application/BulkScoringRequests/SourceConnections/SourceConnectionHandlers.cs` and `dotnet/src/Web.Server/Middleware/BulkSourceClientAddress.cs` (depends on T023 and T031).
- [X] T046 [US1] Implement encrypted pending/request MSAL cache persistence, rotation, touch, expiry, abandon, and discard in `dotnet/src/Infrastructure/SharePoint/Authorization/PendingSourceAuthorizationStore.cs` and `RequestAuthorizationStore.cs` (depends on T022, T024, T032, and T044).
- [X] T047 [US1] Implement delegated-only exact-folder Graph resolution, lineage-bound enumeration/download, safe throttling, and disabled redirects in `dotnet/src/Application/Common/Interfaces/ISharePointSource.cs` and `dotnet/src/Infrastructure/SharePoint/GraphSharePointSource.cs` (depends on T033 and T044).
- [X] T048 [US1] Implement bounded explicit discovery with frozen guards and encrypted traversal cursor in `dotnet/src/Application/BulkScoringRequests/Services/DiscoveryStepService.cs` and `BulkScoringHandlers.cs` (depends on T034 and T047).
- [X] T049 [US1] Implement read-only signed draft pagination with stable snapshot/order and zero source effects in `dotnet/src/Application/BulkScoringRequests/Queries/DraftItemReviewQuery.cs` (depends on T035 and T048).
- [X] T050 [US1] Implement atomic confirmation with fresh job/folder access, configuration capture, recorded submit actor ID and UTC time, request/items/authorization creation, and pending-cache consume in `dotnet/src/Application/BulkScoringRequests/Services/BulkScoringAccess.cs` and `dotnet/src/Infrastructure/Persistence/Repositories/BulkScoringRepository.cs` (depends on T036, T046, and T048).
- [X] T051 [US1] Map P1 endpoints and sanitized RFC 9457 problems, enforce the disclosure matrix, and implement the typed append-only ledger writer/transaction hooks in `dotnet/src/Web.Server/Endpoints/SourceConnectionsEndpoints.cs`, `dotnet/src/Web.Server/Endpoints/BulkScoringRequestsEndpoints.cs`, `dotnet/src/Application/BulkScoringRequests/Services/BulkSourceDisclosurePolicy.cs`, and `dotnet/src/Application/BulkScoringRequests/Services/BulkAuditWriter.cs`; emit preserved control-plane rows plus discovered-item and confirmation rows with user or explicit service actor, correlation ID, UTC time, safe versioned payload, cleanup survival, atomic rollback, and SQLite/SQL Server parity (depends on T037 and T045–T050).
- [X] T052 [US1] Implement byte-authoritative settings behavior, atomic append-only `bulk_settings_changed` emission with correlation ID, recorded administrator actor ID, UTC time, safe old/new value facts and provider parity, and MiB presentation in `dotnet/src/Application/BulkScoringRequests/BulkScoringHandlers.cs`, `dotnet/src/Application/BulkScoringRequests/BulkAuditWriter.cs`, and `dotnet/src/Web.Client/Pages/UploadSettings.razor` (depends on T038).
- [X] T053 [US1] Implement typed API and Blazor redirect/resume, review, discover-more, limit/configuration, confirm, and abandon UI in `dotnet/src/Web.Client/Services/BulkScoringDtos.cs`, `ApiClient.cs`, `dotnet/src/Web.Client/Pages/JobDetail.razor`, and `Components/BulkScoringRequestDialog.razor` (depends on T042 and T049–T052).
- [X] T054 [US1] Implement FR-010G and FR-010B Stack B post-conversion admission in `dotnet/src/Infrastructure/Rasterization/PdfiumRasterizer.cs`, `dotnet/src/Application/BulkScoringRequests/Services/BulkItemProcessor.cs`, and `dotnet/src/Infrastructure/SharePoint/BulkContentStore.cs`, validating configured media type, PDF magic/signature, structural document validity, positive pages, configured size, page count/order, ±1-point dimensions, per-page SSIM, blank, and crop predicates before application/scoring handoff; atomically emit claim/import, metadata recheck, content-stage, conversion start/complete/fail, quarantine/removal, application/document, scoring-handoff, and applicable item-terminal events with an explicit service actor and durable external-content/outbox decisions; quarantine or remove every invalid output, persist a stable safe failure, prohibit original fallback, and prove zero scoring calls (depends on T037, T039, T047, and T004).
- [X] T055 [US1] Implement FR-010C dual-version persistence, converted-default resolution, and authorized original streaming in `dotnet/src/Application/BulkScoringRequests/Services/BulkItemProcessor.cs`, `dotnet/src/Infrastructure/SharePoint/BulkContentStore.cs`, `dotnet/src/Web.Server/Endpoints/BulkScoringRequestsEndpoints.cs`, and `dotnet/src/Web.Client/Pages/ApplicationDetail.razor` (depends on T040 and T054).
- [X] T056 [US1] Implement canonical raw-original-byte SHA-256 and compatible historical lookup across Stack B legacy, optional, and bulk paths in `dotnet/src/Application/Common/Services/ContentFingerprint.cs`, `dotnet/src/Application/Applications/Commands/UploadApplicationsCommand.cs`, `dotnet/src/Application/Uploads/Commands/UploadItemContentCommand.cs`, and `dotnet/src/Application/BulkScoringRequests/Services/BulkItemProcessor.cs`; atomically emit `bulk_item_fingerprint_recorded`, `bulk_item_duplicate_claimed`, and `bulk_item_duplicate_skipped` without exposing fingerprint/name/content facts (depends on T037, T041, and T021).
- [X] T057 [US1] Run the executable Stack B P1 red-to-green gate and record command output in `specs/001-sharepoint-bulk-scoring/validation/stack-b-us1-v3.md` (depends on T029–T056); validation MUST run `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRasterFidelityTests|FullyQualifiedName~ConfirmBulkScoringRequestTests|FullyQualifiedName~BulkAuditContractTests|FullyQualifiedName~SourceConnectionEndpointsTests"` and prove FR-010G quarantine/removal with zero scoring, both SC-002 timestamps on one stopwatch, and every applicable expanded P1 audit row with atomic rollback/outbox/reconciliation evidence, correlation ID, user or explicit service actor, UTC timestamp, immutability/survival, prohibited-payload scan, safe facts, and SQLite/SQL Server parity.

**US1-B-GATE / MVP**: T029–T057 depends on FOUNDATION-GATE.

---

## Phase 4: Stack B — User Story 2: Continue Without the Recruiter's Device (Priority: P2)

**Goal**: Continue request-bound delegated work after device/host interruption while source-
independent staged work finishes after authorization lapse.

**Independent Test**: Close the browser, restart the host, revoke access in every phase, and prove
all new source reads stop, unstaged items fail, staged items finish, and no fallback occurs.

- [X] T058 [P] [US2] Add and run failing request-cache silent renewal, tenant authority, binding, rotation, lapse/revoke/access-loss, discard, and authorization establish/use/lapse/discard immutable-audit tests in `dotnet/tests/Infrastructure.Tests/SharePoint/RequestAuthorizationStoreTests.cs`, requiring correlation ID, authenticated user actor for establishment, explicit worker/timer service actor for use/lapse/discard, optional initiating user, UTC timestamp, update/delete rejection and survival after ciphertext cleanup, versioned safe payload, and SQLite/SQL Server parity; record `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~RequestAuthorizationStoreTests"` failing for the intended gaps in `specs/001-sharepoint-bulk-scoring/validation/stack-b-us2-v3.md` before implementation (depends on T057).
- [X] T059 [P] [US2] Add failing phase-aware lapse tests for pre-stage, staged, converting, scoring, and completed items with immediate Graph stop and no fallback in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkAuthorizationLapseTests.cs` (depends on T057).
- [X] T060 [P] [US2] Add failing captured/current/used scoring-configuration tests in `dotnet/tests/Application.Tests/BulkScoringRequests/BulkScoringConfigurationVersionTests.cs` (depends on T057).
- [X] T061 [P] [US2] Add and run failing worker restart, item claim/lease renew/recover/release, import start, metadata recheck outcomes, content stage/delete, conversion, application/document creation, scoring handoff/start ownership, item terminal state, count reconciliation, and idempotency event tests in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkScoringWorkerTests.cs`; require service actors, atomic state/event or durable outbox/content decisions, prohibited-payload scans, and both providers before implementation (depends on T057).
- [X] T062 [P] [US2] Add failing terminal/cancel-settled authorization cleanup, expiry, audit, and safe lapse notice tests in `dotnet/tests/Application.Tests/BulkScoringRequests/BulkAuthorizationCleanupTests.cs` (depends on T057).
- [X] T063 [US2] Implement bound silent token acquisition and encrypted cache rotation with no application/product/other-user fallback in `dotnet/src/Infrastructure/SharePoint/Authorization/RequestAuthorizationStore.cs` (depends on T058).
- [X] T064 [US2] Make `SourceStoredAt` the durable source boundary and conversion/scoring consume only staged product-owned content in `dotnet/src/Application/BulkScoringRequests/Services/BulkItemProcessor.cs` and `dotnet/src/Infrastructure/SharePoint/BulkContentStore.cs` (depends on T059).
- [X] T065 [US2] Resolve and persist current scoring configuration atomically at handoff in `dotnet/src/Application/BulkScoringRequests/Services/BulkItemProcessor.cs` and `dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs` (depends on T060 and T064).
- [X] T066 [US2] Implement phase-aware lapse/access loss that blocks further Graph work, fails only source-dependent items, and lets staged work settle in `dotnet/src/Application/BulkScoringRequests/Services/BulkAuthorizationLapseService.cs` and `BulkScoringCycle.cs` (depends on T059, T063, and T064).
- [X] T067 [US2] Preserve lease recovery and idempotent stage/convert/score markers in `dotnet/src/Infrastructure/HostedServices/BulkScoringWorker.cs` and `dotnet/src/Application/BulkScoringRequests/Services/BulkRetryPolicy.cs`; emit every applicable item claim/lease/import/metadata/content/application/document/scoring/terminal/count event atomically with state or its durable external boundary using explicit service actors (depends on T061 and T065–T066).
- [X] T068 [US2] Implement terminal/cancel-settled authorization cleanup and all authorization establish/use/lapse/discard append-only events with correlation ID, authenticated user actor for establishment, explicit worker/timer service actor for background mutations, optional initiating user, UTC timestamp, versioned safe payload, survival after ciphertext removal, and provider parity plus a safe lapse notice in `dotnet/src/Application/BulkScoringRequests/Services/BulkAuthorizationCleanupService.cs`, `dotnet/src/Application/BulkScoringRequests/Services/BulkAuditWriter.cs`, and `dotnet/src/Web.Client/Components/BulkScoringRequestNotification.razor` (depends on T058, T062, and T066).
- [X] T069 [US2] Run the executable Stack B P2 red-to-green gate and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-b-us2-v3.md` (depends on T058–T068); validation MUST run `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~RequestAuthorizationStoreTests|FullyQualifiedName~BulkAuthorizationLapseTests|FullyQualifiedName~BulkAuthorizationCleanupTests|FullyQualifiedName~BulkScoringWorkerTests"` and prove every applicable authorization and item/content lifecycle row meets atomicity, correlation-ID, user-or-explicit-service-actor, UTC, immutability/survival, prohibited-payload, and SQLite/SQL Server parity requirements.

**US2-B-GATE**: T058–T069 depends on T050 and US1-B-GATE.

---

## Phase 5: Stack B — User Story 3: Monitor, Cancel, Recover, and Notify (Priority: P3)

**Goal**: Provide authorized detail, accurate progress, phase-aware cancellation/retry, actor
provenance, and one submitter-only terminal notification.

**Independent Test**: Reconcile mixed outcomes, inspect authorized submit/cancel details and both
actor fallback paths, cancel work, retry eligible items, and acknowledge exactly one frozen
first-terminal notification.

### Tests first

- [X] T070 [P] [US3] Add failing summary/detail/item query tests for authorization/configuration states, stable errors, pagination, request-count reconciliation events, and safe reconciled counts in `dotnet/tests/Web.Tests/BulkScoringRequestsQueryEndpointsTests.cs` (depends on T057).
- [X] T071 [P] [US3] Add failing phase-aware cancellation/finalization tests requiring item cancelled/skipped/failed/completed and request count-reconciled/terminal-finalized events in `dotnet/tests/Application.Tests/BulkScoringRequests/CancelBulkScoringRequestTests.cs` (depends on T057).
- [X] T072 [P] [US3] Add failing reconnect-to-new-request retry tests with immutable original request, no old-cache reuse, and typed retry-request plus per-retry-item creation events in `dotnet/tests/Application.Tests/BulkScoringRequests/RetryFailedBulkScoringRequestTests.cs` (depends on T057).
- [X] T073 [P] [US3] Add failing bUnit monitoring, cancellation, retry, pagination, accessibility, and ≤30-second polling tests in `dotnet/tests/Web.Tests/BulkScoringRequestsPanelTests.cs` (depends on T057).
- [X] T074 [P] [US3] Add failing FR-019/SC-031 Stack B tests proving submit and cancel recorded actor ID plus UTC timestamp appear only on authorized detail and resolve display name solely by looking up that recorded actor ID, falling back only to the recorded actor ID for a missing user or blank display name and never to username, while unauthorized/list/notification/admin surfaces expose none, in `dotnet/tests/Application.Tests/BulkScoringRequests/BulkRequestActorDetailTests.cs` and `dotnet/tests/Web.Tests/BulkRequestActorDetailEndpointsTests.cs` (depends on T020 and T057).
- [X] T075 [P] [US3] Add and run failing FR-020/SC-032 Stack B tests in `dotnet/tests/Application.Tests/BulkScoringRequests/BulkTerminalNotificationTests.cs` and `dotnet/tests/Web.Tests/BulkTerminalNotificationEndpointsTests.cs` for exactly one submitter-only first-terminal notification, atomically frozen completed/failed/skipped/cancelled counts, server-generated authorized relative detail link, structural absence of any item-name DTO field, retained idempotent acknowledgment, and confirmation/cancellation/retry/count-reconciled/terminal-finalized/notification creation/acknowledgment audit rows with correlation ID, authenticated user actor for user actions, explicit finalizer service actor for background actions, UTC timestamp, immutable survival, safe payload, and SQLite/SQL Server parity; record the intentional red command in `specs/001-sharepoint-bulk-scoring/validation/stack-b-us3-v3.md` before implementation (depends on T020 and T057).

### Implementation

- [X] T076 [US3] Implement authorized summary/detail/item projections with safe states, configuration evidence, and reconciled counts in `dotnet/src/Application/BulkScoringRequests/Queries/BulkScoringQueries.cs` (depends on T070 and T065–T068).
- [X] T077 [US3] Implement phase-aware cancellation and finalization with explicit worker-service item-terminal, request-count-reconciled, and first terminal-finalized events in `dotnet/src/Application/BulkScoringRequests/Commands/CancelBulkScoringRequestCommand.cs` and `Services/BulkScoringCycle.cs` (depends on T071 and T066).
- [X] T078 [US3] Implement newly bound retry filtering that preserves the original request and atomically emits the user-attributed retry-request event plus every retry-item-created event in `dotnet/src/Application/BulkScoringRequests/Commands/RetryFailedBulkScoringRequestItemsCommand.cs` (depends on T072 and T046–T050).
- [X] T079 [US3] Implement FR-019 submit/cancel recorded actor ID and UTC time persistence plus authorized batched user projection that queries solely by the recorded actor ID, returns display name when nonblank, otherwise returns that recorded ID, and never reads username as fallback in `dotnet/src/Application/BulkScoringRequests/Queries/BulkScoringQueries.cs`, `dotnet/src/Application/BulkScoringRequests/Commands/CancelBulkScoringRequestCommand.cs`, and `dotnet/src/Infrastructure/Identity/UserDirectory.cs` (depends on T074, T050, and T077).
- [X] T080 [US3] Implement FR-020 first-terminal atomic outcome/count freezing, submitter-only notification query, authorized relative link, item-name-free DTO, conditional idempotent acknowledgment, and append-only cancellation/retry/count-reconciled/terminal-finalized/notification creation/acknowledgment events with correlation ID, authenticated user or explicit finalizer-service actor, UTC timestamp, immutable survival, safe versioned payload, and provider parity in `dotnet/src/Application/BulkScoringRequests/Services/BulkScoringCycle.cs`, `dotnet/src/Application/BulkScoringRequests/Queries/BulkScoringQueries.cs`, `dotnet/src/Application/BulkScoringRequests/Commands/AcknowledgeBulkNotificationCommand.cs`, and `dotnet/src/Application/BulkScoringRequests/Services/BulkAuditWriter.cs` (depends on T075–T077).
- [X] T081 [US3] Map monitoring/cancel/retry/notification/acknowledgment contracts and typed clients in `dotnet/src/Web.Server/Endpoints/BulkScoringRequestsEndpoints.cs`, `dotnet/src/Web.Client/Services/BulkScoringDtos.cs`, and `ApiClient.cs` (depends on T076–T080).
- [X] T082 [US3] Implement monitoring, authorized actor detail, cancellation/retry, and submitter-only notification UI in `dotnet/src/Web.Client/Components/BulkScoringRequestsPanel.razor` and `BulkScoringRequestNotification.razor` (depends on T073 and T081).
- [X] T083 [US3] Run the executable Stack B P3 red-to-green gate and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-b-us3-v3.md` (depends on T070–T082); validation MUST run `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRequestActorDetail|FullyQualifiedName~BulkTerminalNotification|FullyQualifiedName~CancelBulkScoringRequest|FullyQualifiedName~RetryFailedBulkScoringRequest"` and prove recorded-actor-ID-only display resolution with no username fallback plus every applicable item-terminal, retry-item, count-reconciled, terminal-finalized, and notification audit row on SQLite and SQL Server with atomicity and every Principle I field/survival/payload-safety assertion.

**US3-B-GATE**: T070–T083 depends on US1-B-GATE; settlement and notification finalization also
depend on US2-B-GATE.

---

## Phase 6: Stack B Infrastructure, Old-Mode Removal, and Release Proof

### Tests and assertions first

- [X] T084 [P] Add delegated-only connector Terraform assertions and deterministic discovery/validation of `infra/terraform/live/shared`, `infra/terraform/live/stack-b`, and every directory classified as a Stack B Terraform root in `infra/terraform/tests/assert-source-connector.mjs` and `infra/terraform/tests/validate-stack-b-roots.ps1` (depends on T028).
- [X] T085 [P] Add old reader/OBO/app-only/`Sites.Selected`/allow-list/grant removal assertions in `infra/terraform/tests/assert-no-legacy-sharepoint-access.mjs` (depends on T028).
- [X] T086 [P] Add failing migration inventory/refusal/drain/discard, completed-history, and safe telemetry/audit tests in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkSourceOperationsTests.cs` (depends on T028).
- [X] T087 [P] Add and run failing FR-021 SQLite/Azure SQL job-lifetime retention, worker fencing, relational/staged-content cleanup, reconciliation, repeated deletion, and detached audit tests in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkJobDeletionTests.cs`; require original/converted/transient deletion, item/request correction, content-cleanup reconciliation pending/completed, and preserved `bulk_job_cleanup_completed|pending` rows with correlation ID, authenticated deleting user provenance plus explicit cleanup/reconciliation service actor, UTC timestamp, atomic durable decisions, append-only update/delete rejection, survival after job/content cleanup, prohibited-payload scans, versioned safe payload, and provider parity; record the failing command in `specs/001-sharepoint-bulk-scoring/validation/stack-b-security-infrastructure-v3.md` before implementation (depends on T021).

### Infrastructure, removal, and operations

- [X] T088 Provision/adopt one `AzureADMultipleOrgs` confidential connector with exact Web callbacks, delegated scopes, and managed-identity federation in `infra/terraform/modules/foundation/entra/main.tf`, `variables.tf`, `outputs.tf`, and `infra/terraform/modules/stack-b/main.tf` (depends on T084).
- [X] T089 Retain Key Vault wrap/unwrap RBAC and blank Stack B connector settings in `infra/terraform/modules/foundation/key-vault/main.tf`, `infra/terraform/live/stack-b/main.tf`, and `variables.tf` (depends on T088).
- [X] T090 Remove Stack B old OBO/application-only readers, fallback selectors, same/cross modes, and source allow-list settings/tests from `dotnet/src/Infrastructure/SharePoint/`, `dotnet/src/Application/BulkScoringRequests/BulkScoringOptions.cs`, and `dotnet/tests/` (depends on T085 and T069).
- [X] T091 Remove reader registration, Graph application roles, `Sites.Selected`, permitted roots/hosts, site-grant wiring, passwords, and certificates from `infra/terraform/modules/foundation/entra/`, `infra/terraform/live/stack-b/`, and `infra/scripts/` (depends on T085 and T088).
- [X] T092 Replace tracked templates with blank connector/callback/TTL/admission/limit variables in `.env.example`, `.env_local.example`, `.env_qa.example`, and `.env_prod.example` (depends on T090–T091).
- [X] T093 Implement and document migration inventory, drain/cancel, protected-material discard, schema migration, completed-history verification, and non-restorative rollback in `dotnet/src/Web.Server/Operations/BulkSourceMigrationPreflight.cs` and `specs/001-sharepoint-bulk-scoring/validation/migration-runbook-v3.md` (depends on T027 and T086).
- [X] T094 Switch only activated Stack B operational sections to the delegated connector in `docs/SHAREPOINT_BULK_SCORING_APP_REGISTRATION.md`, `README.md`, `AUTHENTICATION.md`, and `INTEGRATION.md` (depends on T090–T093).
- [X] T095 Implement safe lifecycle telemetry/audit/alerts without URL/name/email/tenant/token dimensions in `dotnet/src/Application/BulkScoringRequests/Services/BulkTelemetry.cs` and `dotnet/src/Infrastructure/SharePoint/Authorization/SourceAuthorizationTelemetry.cs` (depends on T086 and T051).
- [X] T096 Implement FR-021 Stack B job deletion with worker fencing, provider cascades, original/converted/transient cleanup, reconciliation, idempotency, and every required content-deleted, item/request-corrected, content-reconciliation, and preserved job-cleanup event with correlation ID, authenticated deleting user provenance plus explicit cleanup/reconciliation service actor, UTC timestamp, durable decision/outcome atomicity, versioned safe payload, immutable survival, and provider parity in `dotnet/src/Application/Jobs/Commands/DeleteJobCommand.cs`, `dotnet/src/Application/BulkScoringRequests/Services/BulkAuditWriter.cs`, `dotnet/src/Infrastructure/Persistence/Repositories/BulkScoringRepository.cs`, and `dotnet/src/Infrastructure/SharePoint/BulkContentStore.cs` (depends on T087 and T021).
- [X] T097 Run Stack B infrastructure/removal/security validation and record `specs/001-sharepoint-bulk-scoring/validation/stack-b-security-infrastructure-v3.md` (depends on settings/audit work T052, P1 audit gate T057, authorization audit gate T069, P3 audit gate T083, assertion/root-discovery tests T084–T085, removals T090–T091, blank settings/configuration T092, migration T093, activated operational docs T094, telemetry T095, and deletion/audit work T096); validation MUST run `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkAuditContractTests|FullyQualifiedName~RequestAuthorizationStoreTests|FullyQualifiedName~BulkTerminalNotificationTests|FullyQualifiedName~BulkJobDeletionTests|FullyQualifiedName~BulkSourceOperationsTests"`, `node infra/terraform/tests/assert-source-connector.mjs`, `node infra/terraform/tests/assert-no-legacy-sharepoint-access.mjs`, `terraform fmt -check -recursive infra/terraform`, `terraform -chdir=infra/terraform/live/shared validate`, `terraform -chdir=infra/terraform/live/stack-b validate`, `pwsh -NoProfile -File infra/terraform/tests/validate-stack-b-roots.ps1`, and `git diff --check`; every command MUST exit 0, the root script MUST fail if either named root is absent or any discovered Stack B root fails, and evidence MUST verify `docs/SHAREPOINT_BULK_SCORING_APP_REGISTRATION.md`, `README.md`, `AUTHENTICATION.md`, and `INTEGRATION.md` no longer present removed Stack B paths as current.
- [X] T098 Run the complete executable Stack B automated gate and record exact versions/results in `specs/001-sharepoint-bulk-scoring/validation/b-gate-automated-v3.md` (depends on T057, T069, T083, and T097); validation MUST run `dotnet test dotnet/TalentMatch.slnx`, `dotnet build dotnet/TalentMatch.slnx -c Release`, and the focused complete-matrix command from T097, with every command exiting 0 and every expanded request/item/content audit row green on SQLite and Azure SQL.
- [ ] T099 Run STACK-B-LIVE-GATE as the distinct live Stack B browser→API→worker delegated SharePoint and work-OneDrive journey, including another tenant, shared-not-owned folder, background continuation, renewal, access removal, lapse, duplicates, conversion success/failure/quarantine/removal, scoring handoff, mixed terminal outcomes, staged-content cleanup, and reconciliation; correlate every expanded matrix row to sanitized T098 provider evidence without recording tokens, raw URLs, item names, fingerprints, or content in `specs/001-sharepoint-bulk-scoring/validation/live-stack-b-delegated-v3.md` (depends on T098).
- [ ] T100 Issue B-GATE PASS only after activated operational documentation T094, complete infrastructure/audit validation T097, automated gate T098, and STACK-B-LIVE-GATE T099 pass, recording the decision in `specs/001-sharepoint-bulk-scoring/validation/b-gate-v3.md` and `PARITY.md`; every expanded Stack B control-plane/request/item/content row, atomicity boundary, actor type/ID, correlation, UTC time, immutable retention, prohibited-payload scan, and provider result is mandatory and cannot be waived (depends on T094 and T097–T099).

**STACK-B-LIVE-GATE**: T099 requires the complete expanded matrix and cannot be satisfied by
historical or automated-only evidence.

**B-GATE**: T100 blocks every Stack A removal/runtime task. Historical live evidence cannot replace
T099, and the former control-plane-only matrix cannot pass.

---

## Phase 7: Stack A Old-Mode Removal — Blocked by B-GATE

- [ ] T101 Add failing Stack A inventory assertions for old readers, OBO/app-only paths, same/cross modes, `Sites.Selected`, allow-lists, credentials, fallback tests, and current docs in `tests/security/stack-a-old-source-removal.test.ts` (depends on B-GATE T100).
- [ ] T102 Remove Stack A source credentials, same/cross and host/root/site/drive settings/columns from `server/types.d.ts`, `server/storage/schema.sql`, `server/storage/schema-sqlite.sql`, `.env.example`, and `src/types/index.ts` (depends on T101).
- [ ] T103 Delete old Stack A source readers/fallback registration under `server/services/` and `server/index.ts` (depends on T101–T102).
- [ ] T104 Remove obsolete Stack A old-mode tests/fixtures while retaining shared delegated fixtures in `tests/integration/`, `tests/unit/`, and `tests/fixtures/bulk-scoring-requests/` (depends on T101–T103).
- [ ] T105 Remove Stack A grant/allow-list infrastructure and switch activated Stack A operational sections in `infra/scripts/grant-sharepoint-site.sh`, `docs/SHAREPOINT_BULK_SCORING_APP_REGISTRATION.md`, `AUTHENTICATION.md`, `INTEGRATION.md`, and `README.md` only after removal (depends on T101–T104).
- [ ] T106 Record STACK-A-REMOVAL-GATE in `specs/001-sharepoint-bulk-scoring/validation/stack-a-old-mode-removal-v3.md`; validation: Stack A removal, repository-policy, no-legacy Terraform, and `git diff --check` commands exit 0 (depends on T101–T105).

---

## Phase 8: Stack A Provider and Lifecycle Foundation — Blocked by B-GATE

- [ ] T107 [P] Add and run failing Stack A FR-021 SQLite/Azure SQL deletion/provider tests in `tests/integration/bulk-job-deletion.test.ts`, including original/converted/transient deletion, item/request corrections, content-cleanup reconciliation pending/completed, and preserved `bulk_job_cleanup_completed|pending` rows with correlation ID, authenticated deleting user provenance plus explicit cleanup/reconciliation service actor, UTC timestamp, atomic durable decisions, update/delete rejection, survival after job/content cleanup, prohibited-payload scans, versioned safe payload, and provider parity; record the command failing for intended gaps in `specs/001-sharepoint-bulk-scoring/validation/stack-a-foundation-v3.md` before implementation (depends on T106).
- [ ] T108 [P] Add failing Stack A publisher-gate matrix and no-source-input signature tests in `tests/integration/source-publisher-gate.test.ts` (depends on T106).
- [ ] T109 Implement Stack A FR-021 worker fencing, provider cascades, staged cleanup, reconciliation, idempotency, and every required content-deleted, item/request-corrected, content-reconciliation, and preserved job-cleanup event with correlation ID, authenticated deleting user provenance plus explicit cleanup/reconciliation service actor, UTC timestamp, durable decision/outcome atomicity, versioned safe payload, immutable survival, and SQLite/Azure SQL parity in `server/routes/jobs.ts`, `server/storage/bulk-scoring-repo.ts`, `server/services/blob-store.ts`, `server/services/document-storage.ts`, and `server/services/bulk-audit-writer.ts` (depends on T107).
- [ ] T110 Implement the Stack A deployment-environment/explicit-assertion activation gate before route/worker registration in `server/services/source-publisher-gate.ts` and `server/index.ts` (depends on T108).
- [ ] T111 Run the executable Stack A foundation provider/publisher gate and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-a-foundation-v3.md` (depends on T107–T110); validation MUST run `npx vitest run tests/integration/bulk-job-deletion.test.ts tests/integration/source-publisher-gate.test.ts` for both configured providers and require every command to exit 0.
- [ ] T112 Issue STACK-A-FOUNDATION-GATE in `specs/001-sharepoint-bulk-scoring/validation/stack-a-foundation-gate-v3.md` (depends on T111).

---

## Phase 9: Stack A — User Story 1 Parity (Priority: P1, Blocked by B-GATE)

**Goal**: Match Stack B connection, discovery/review, configuration, settings, raster, content, and
fingerprint behavior.

**Independent Test**: Replay Stack B P1 fixtures through Express/React and compare statuses, errors,
pages, limits, raster verdicts, content versions, and safe fields.

### Tests first

- [ ] T113 [P] [US1] Add failing Stack A seven-table schema, repository, admission, expiry, atomic consume, review/configuration, and history tests in `tests/integration/bulk-source-connection-storage.test.ts` (depends on T112).
- [ ] T114 [P] [US1] Add failing MSAL Node `/organizations`, PKCE/state, consent, commercial cloud, encrypted cache, binding, expiry, and discard tests in `tests/integration/source-connector.test.ts` (depends on T112).
- [ ] T115 [P] [US1] Add failing Stack A admission-order and no-rejected-external-work tests in `tests/integration/source-connection-admission.test.ts` (depends on T112).
- [ ] T116 [P] [US1] Add failing delegated exact-folder SharePoint/work-OneDrive and bounded discovery tests in `tests/integration/bulk-scoring-discovery.test.ts` (depends on T112).
- [ ] T117 [P] [US1] Add and run failing stable draft-review, route, error, disclosure, authorization-state, SC-002 timing, and Stack A immutable-audit tests in `tests/integration/bulk-scoring-draft-review.test.ts`, `tests/integration/bulk-scoring-routes.test.ts`, `tests/integration/bulk-audit-contract.test.ts`, and `tests/security/bulk-source-disclosure.test.ts`; enumerate every OpenAPI matrix row and require each applicable P1 discovered-item/claim/import/metadata/content/fingerprint/duplicate/conversion/quarantine/removal/application/document/scoring-handoff/item-terminal row, atomic rollback or durable outbox/reconciliation boundary, correlation ID, user or explicit service actor, optional initiating user, UTC timestamp, update/delete rejection, cleanup survival, prohibited-payload scan, versioned safe facts, and SQLite/Azure SQL parity; record the named commands failing for intended gaps in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us1-v3.md` before implementation (depends on T112).
- [ ] T118 [P] [US1] Add failing React redirect/review/discover-more/settings/default/accessibility tests in `tests/unit/bulk-scoring-source-connection.test.tsx` and `tests/unit/optional-upload-settings-ui.test.tsx` (depends on T112).
- [ ] T119 [P] [US1] Add and run failing FR-010G plus FR-010B/SC-030 Stack A tests in `tests/integration/bulk-raster-fidelity.test.ts` for configured output type, PDF magic/signature, structural document validity, positive pages, configured size, exact page count/order, dimensions ±1 point, every-page SSIM >=0.95, blank/crop rejection, quarantine or removal of invalid output, no original fallback, and zero application/scoring handoff; record `npx vitest run tests/integration/bulk-raster-fidelity.test.ts` failing for the intended gaps in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us1-v3.md` before implementation (depends on T004 and T112).
- [ ] T120 [P] [US1] Add failing Stack A FR-010C retained-original/converted-default/authorized-download/page-count/TXT tests in `tests/integration/bulk-content-versions.test.ts` and `tests/unit/document-viewer-versions.test.tsx` (depends on T112).
- [ ] T121 [P] [US1] Add failing Stack A FR-010H legacy/optional/bulk/pre-change/provider matrix tests in `tests/integration/bulk-fingerprint-compatibility.test.ts` (depends on T005 and T112).

### Implementation

- [ ] T122 [US1] Implement shared-schema repositories, optimistic concurrency, and atomic transfer in `server/storage/schema-sqlite.sql`, `server/storage/schema.sql`, `server/storage/db.ts`, `server/storage/bulk-source-connections.ts`, and `server/storage/bulk-scoring-repo.ts` (depends on T113).
- [ ] T123 [US1] Implement HMAC admission storage and pre-external-work ordering in `server/storage/bulk-source-admission.ts` and `server/services/source-admission.ts` (depends on T115 and T122).
- [ ] T124 [US1] Implement confidential MSAL connector, classifier, PKCE/state, consent resume, and encrypted caches in `server/services/microsoft365-source-connector.ts`, `commercial-cloud-classifier.ts`, and `source-authorization-store.ts` (depends on T114 and T122).
- [ ] T125 [US1] Implement delegated-only exact-folder Graph resolution and lineage download in `server/services/sharepoint/graph-source.ts` (depends on T116 and T124).
- [ ] T126 [US1] Implement bounded discovery and signed independent review paging in `server/services/bulk-scoring-discovery.ts` and `server/services/bulk-scoring-draft-review.ts` (depends on T116–T117 and T122–T125).
- [ ] T127 [US1] Implement atomic confirmation with recorded submit actor ID/UTC time, SC-002 durable-before-acknowledgment behavior, P1 source/callback/review/confirm endpoints, and the typed append-only ledger writer/transaction hooks in `server/services/bulk-scoring.ts`, `server/services/bulk-audit-writer.ts`, `server/routes/source-connections.ts`, and `server/routes/bulk-scoring-requests.ts`; emit preserved control-plane plus discovered-item/confirmation rows with user or explicit service actor, correlation ID, UTC timestamp, immutable survival, safe payload, atomic rollback, and provider parity (depends on T117 and T124–T126).
- [ ] T128 [US1] Apply the disclosure matrix to Stack A serializers, audit, redirects, logs, metrics, and admin surfaces in `server/services/bulk-source-disclosure.ts` and `server/services/bulk-source-telemetry.ts` (depends on T117 and T127).
- [ ] T129 [US1] Implement settings parity with atomic append-only `bulk_settings_changed` emission containing correlation ID, recorded administrator actor ID, UTC timestamp, safe old/new value facts, cleanup survival, and provider parity plus typed React redirect/review/confirm/abandon UX in `server/routes/upload-settings.ts`, `server/services/bulk-audit-writer.ts`, `src/lib/api-real.ts`, `src/components/BulkScoringRequestDialog.tsx`, and `src/components/OptionalUploadSettings.tsx` (depends on T118 and T127).
- [ ] T130 [US1] Extend Stack A mocks for shared commercial-cloud, consent, admission, pagination, and limit fixtures in `server/services/sharepoint/mock-source.ts` and `server/services/mock-source-connector.ts` (depends on T114–T118).
- [ ] T131 [US1] Implement FR-010G and FR-010B Stack A post-conversion admission in `server/services/rasterize-document.ts`, `server/workers/bulk-scoring.ts`, and `server/services/bulk-content-versions.ts`, validating configured media type, PDF magic/signature, structural document validity, positive pages, configured size, page order/count, ±1-point dimensions, per-page SSIM, blank, and crop predicates before application/scoring handoff; atomically emit claim/import, metadata recheck, content-stage, conversion start/complete/fail, quarantine/removal, application/document, scoring-handoff, and applicable item-terminal events with explicit service actors and durable external-content/outbox decisions; prohibit original fallback and prove zero scoring calls for invalid output (depends on T117, T119, T125, and T004).
- [ ] T132 [US1] Implement Stack A FR-010C dual-version persistence, converted-default viewer, and authorized original streaming in `server/services/bulk-content-versions.ts`, `server/workers/bulk-scoring.ts`, `server/routes/bulk-scoring-requests.ts`, and `src/components/DocumentViewer.tsx` (depends on T120 and T131).
- [ ] T133 [US1] Implement Stack A canonical raw-byte SHA-256 and compatible historical lookup in `server/services/content-fingerprint.ts`, `server/routes/applications.ts`, `server/services/optional-upload.ts`, `server/workers/bulk-scoring.ts`, and `server/storage/repos/application-repo.ts`; atomically emit fingerprint-recorded, duplicate-claimed, and duplicate-skipped rows without fingerprint/name/content facts (depends on T117, T121, and T122–T127).
- [ ] T134 [US1] Run the executable Stack A P1 red-to-green gate and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us1-v3.md` (depends on T113–T133); validation MUST run `npx vitest run tests/integration/bulk-raster-fidelity.test.ts tests/integration/bulk-scoring-routes.test.ts tests/integration/bulk-audit-contract.test.ts` against SQLite and Azure SQL fixtures and prove FR-010G quarantine/removal with zero scoring, both SC-002 timestamps on one stopwatch, and every applicable expanded P1 audit row with atomicity, correlation ID, user or explicit service actor, UTC timestamp, immutability/survival, prohibited-payload scan, safe facts, and provider parity.
- [ ] T135 [US1] Compare Stack A P1 output to Stack B snapshots in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us1-contract-parity-v3.md` (depends on T134).

**US1-A-GATE**: T113–T135 depends on STACK-A-FOUNDATION-GATE and therefore B-GATE.

---

## Phase 10: Stack A — User Story 2 Parity (Priority: P2, Blocked by B-GATE)

- [ ] T136 [P] [US2] Add and run failing Stack A request-cache renewal/binding/rotation/lapse/discard and authorization establish/use/lapse/discard immutable-audit tests in `tests/integration/source-request-authorization.test.ts`, requiring correlation ID, authenticated user actor for establishment, explicit worker/timer service actor for use/lapse/discard, optional initiating user, UTC timestamp, update/delete rejection and survival after ciphertext cleanup, versioned safe payload, and SQLite/Azure SQL parity; record the command failing for intended gaps in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us2-v3.md` before implementation (depends on T135).
- [ ] T137 [P] [US2] Add failing phase-aware staged-versus-unstaged Graph-stop/no-fallback worker tests in `tests/integration/bulk-scoring-worker-authorization.test.ts` (depends on T135).
- [ ] T138 [P] [US2] Add and run failing configuration-version, restart, item claim/lease renew/recover/release, import/metadata/content/conversion/application/document/scoring/terminal/count, and idempotency event tests in `tests/integration/bulk-scoring-worker-continuity.test.ts`, requiring explicit service actors, atomic state/event or durable outbox/content decisions, prohibited-payload scans, and both providers before implementation (depends on T135).
- [ ] T139 [US2] Implement bound renewal, cache rotation, terminal discard, and authorization establish/use/lapse/discard append-only events with correlation ID, authenticated user actor for establishment, explicit worker/timer service actor for background mutations, optional initiating user, UTC timestamp, versioned safe payload, survival after ciphertext removal, and provider parity in `server/services/source-authorization-store.ts` and `server/services/bulk-audit-writer.ts` (depends on T136).
- [ ] T140 [US2] Implement durable staging and phase-aware lapse/access loss without fallback in `server/workers/bulk-scoring.ts` and `server/services/bulk-authorization-lapse.ts` (depends on T137 and T139).
- [ ] T141 [US2] Resolve/store current configuration at each handoff and expose version differences in `server/workers/bulk-scoring.ts` and `server/services/bulk-scoring.ts` (depends on T138 and T140).
- [ ] T142 [US2] Preserve lease/idempotency and implement safe lapse notice plus 15-minute cleanup in `server/workers/bulk-scoring.ts` and `server/services/bulk-authorization-cleanup.ts`; emit every applicable item claim/lease/import/metadata/content/application/document/scoring/terminal/count event atomically with state or its durable external boundary using explicit service actors (depends on T137–T141).
- [ ] T143 [US2] Expose safe configuration/lapse notices in `src/lib/api.ts` and `src/components/BulkScoringRequestNotification.tsx` (depends on T142).
- [ ] T144 [US2] Run the executable Stack A P2 red-to-green gate and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us2-v3.md` (depends on T136–T143); validation MUST run `npx vitest run tests/integration/source-request-authorization.test.ts tests/integration/bulk-scoring-worker-authorization.test.ts tests/integration/bulk-scoring-worker-continuity.test.ts` for both providers and prove every applicable authorization and item/content lifecycle row satisfies atomicity, user-or-explicit-service actor, every Principle I field, prohibited-payload, and survival assertions.
- [ ] T145 [US2] Compare Stack A P2 state transitions with Stack B in `specs/001-sharepoint-bulk-scoring/validation/stack-a-story-parity-v3.md` (depends on T144).

**US2-A-GATE**: T136–T145 depends on US1-A-GATE.

---

## Phase 11: Stack A — User Story 3 Parity (Priority: P3, Blocked by B-GATE)

### Tests first

- [ ] T146 [P] [US3] Add failing Stack A monitoring/query/authorization/configuration/count tests with request-count-reconciled and terminal-finalized events in `tests/integration/bulk-scoring-monitoring.test.ts` (depends on T135).
- [ ] T147 [P] [US3] Add failing Stack A cancellation and reconnect/retry tests with every item terminal row plus retry-request and per-retry-item creation rows in `tests/integration/bulk-scoring-cancel-retry.test.ts` (depends on T135).
- [ ] T148 [P] [US3] Add failing React monitoring, cancellation, retry, pagination, accessibility, and polling tests in `tests/unit/bulk-scoring-panel.test.tsx` (depends on T135).
- [ ] T149 [P] [US3] Add failing FR-019/SC-031 Stack A tests for detail-only submit/cancel recorded actor ID plus UTC timestamp and display name resolved solely by looking up that recorded ID, falling back only to the recorded ID for a missing user or blank display name and never to username, in `tests/integration/bulk-request-actor-detail.test.ts` and `tests/unit/bulk-request-actor-detail.test.tsx` (depends on T135).
- [ ] T150 [P] [US3] Add and run failing FR-020/SC-032 Stack A tests in `tests/integration/bulk-terminal-notification.test.ts` and `tests/unit/bulk-terminal-notification.test.tsx` for one submitter-only first-terminal notification, frozen counts, authorized relative link, no item-name DTO field, idempotent retained acknowledgment, and confirmation/cancellation/retry/count-reconciled/terminal-finalized/notification creation/acknowledgment append-only rows with correlation ID, authenticated user actor for user actions, explicit finalizer service actor for background actions, UTC timestamp, immutable survival, safe payload, and SQLite/Azure SQL parity; record the intentional red command in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us3-v3.md` before implementation (depends on T135).

### Implementation

- [ ] T151 [US3] Implement monitoring projections with safe authorization/configuration evidence and atomically emitted request-count-reconciled and terminal-finalized rows using an explicit finalizer service actor in `server/services/bulk-scoring.ts` and `server/storage/bulk-scoring-repo.ts` (depends on T146 and T141–T142).
- [ ] T152 [US3] Implement phase-aware cancellation and newly bound retry without old-cache reuse, atomically emitting every item-terminal row and the user-attributed retry-request plus per-retry-item creation rows in `server/services/bulk-scoring.ts` and `server/routes/bulk-scoring-requests.ts` (depends on T147 and T140–T142).
- [ ] T153 [US3] Implement FR-019 submit/cancel recorded actor ID and UTC time provenance plus authorized batched projection that queries solely by the recorded actor ID, returns display name when nonblank, otherwise returns that recorded ID, and never reads username as fallback in `server/services/bulk-request-actor-detail.ts`, `server/services/bulk-scoring.ts`, and `server/routes/bulk-scoring-requests.ts` (depends on T149, T127, and T152).
- [ ] T154 [US3] Implement FR-020 first-terminal frozen counts, submitter-only notification query, authorized relative link, item-name-free DTO, conditional idempotent acknowledgment, and append-only cancellation/retry/count-reconciled/terminal-finalized/notification creation/acknowledgment events with correlation ID, authenticated user or explicit finalizer-service actor, UTC timestamp, immutable survival, versioned safe payload, and provider parity in `server/services/bulk-terminal-notification.ts`, `server/services/bulk-audit-writer.ts`, `server/storage/bulk-scoring-repo.ts`, and `server/routes/bulk-scoring-requests.ts` (depends on T150–T152).
- [ ] T155 [US3] Implement typed monitoring/cancel/retry/notification/acknowledgment clients in `src/lib/api.ts` and `src/lib/api-real.ts` (depends on T151–T154).
- [ ] T156 [US3] Implement monitoring, actor detail, cancellation/retry, and submitter-only notification UI in `src/components/BulkScoringRequestsPanel.tsx` and `BulkScoringRequestNotification.tsx` (depends on T148 and T155).
- [ ] T157 [US3] Run the executable Stack A P3 red-to-green gate and record results in `specs/001-sharepoint-bulk-scoring/validation/stack-a-us3-v3.md` (depends on T146–T156); validation MUST run `npx vitest run tests/integration/bulk-scoring-cancel-retry.test.ts tests/integration/bulk-request-actor-detail.test.ts tests/integration/bulk-terminal-notification.test.ts tests/unit/bulk-request-actor-detail.test.tsx tests/unit/bulk-terminal-notification.test.tsx` for both providers and prove recorded-actor-ID-only display resolution with no username fallback plus every applicable item-terminal, retry-item, count-reconciled, terminal-finalized, and notification row with atomicity and every Principle I field/survival/payload-safety assertion.
- [ ] T158 [US3] Compare Stack A P2/P3 responses and transitions with Stack B in `specs/001-sharepoint-bulk-scoring/validation/stack-a-story-parity-v3.md` (depends on T145 and T157).

**US3-A-GATE**: T146–T158 depends on US1-A-GATE; settlement/notification finalization also depends
on US2-A-GATE.

---

## Phase 12: A-GATE — Stack A Automated and Distinct Live Delegated Gate

- [ ] T159 Run the complete executable Stack A automated gate, including every expanded Stack A control-plane/request/item/content Principle I mutation row on SQLite and Azure SQL, and record exact results in `specs/001-sharepoint-bulk-scoring/validation/a-gate-automated-v3.md` (depends on T111, T134, T144, T145, T157, and T158); validation MUST run `npx vitest run tests/integration/bulk-audit-contract.test.ts tests/integration/source-request-authorization.test.ts tests/integration/bulk-terminal-notification.test.ts tests/integration/bulk-job-deletion.test.ts`, `npm run test`, `npm run build`, `npm run lint`, and `npm run test:e2e`, require every command to exit 0, and prove atomicity, correlation ID, user or explicit service actor, UTC timestamp, immutability/survival, prohibited-payload scan, safe facts, and provider parity for every matrix row.
- [ ] T160 Run STACK-A-LIVE-GATE as a distinct post-implementation Stack A browser→Express→worker live SharePoint/work-OneDrive delegated journey with another tenant, background continuation, renewal, access removal, lapse, duplicates, conversion success/failure/quarantine/removal, scoring handoff, mixed terminal outcomes, staged-content cleanup, and reconciliation; correlate every expanded matrix row to sanitized T159 provider evidence without recording tokens, raw URLs, item names, fingerprints, or content in `specs/001-sharepoint-bulk-scoring/validation/live-stack-a-delegated-v3.md` (depends on T159).
- [ ] T161 Issue A-GATE PASS and record the decision in `specs/001-sharepoint-bulk-scoring/validation/a-gate-v3.md` and `PARITY.md`; every expanded row, atomicity boundary, actor type/ID, correlation, UTC time, immutable retention, prohibited-payload scan, and provider result is mandatory (depends on T159–T160).

**STACK-A-LIVE-GATE**: T160 requires the complete expanded matrix and cannot be satisfied by Stack B
or automated-only evidence.

**A-GATE**: T161. Stack B live evidence cannot substitute for T160, and the former
control-plane-only matrix cannot pass.

---

## Phase 13: Polish, Cross-Stack Parity, Publisher, and Release Gates

**Cross-stack red-first rule**: T162–T170 must be authored and run before T171 parity remediation.
Record at least one intentional failing assertion for each uncovered parity gap. If a suite is
already green, record that no remediation was required; no failure may be waived as aggregation.

- [ ] T162 [P] Add and run red-first cross-stack public contract/state/error/pagination/limit/configuration replay plus the complete Principle I audit mutation matrix in `tests/integration/bulk-scoring-parity.test.ts`, `tests/integration/bulk-audit-contract-parity.test.ts`, `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkScoringParityTests.cs`, and `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkAuditContractParityTests.cs`; enumerate every preserved control-plane row and every OpenAPI request/item/content row from discovered item through claim/import/metadata/content/duplicate/conversion/quarantine/removal/scoring/terminal/retry/count/finalization/deletion/reconciliation across Stack A/Stack B and SQLite/Azure SQL; require atomic state/event or durable outbox/reconciliation evidence, correlation ID, user or explicit service actor, optional initiating user, UTC timestamp, update/delete rejection, cleanup survival, prohibited-payload scans, versioned safe facts, and four-way parity; record the pre-remediation commands in `specs/001-sharepoint-bulk-scoring/validation/audit-contract-parity-v3.md` (depends on T100 and T161).
- [ ] T163 [P] Add and run red-first cross-stack disclosure/security/SSRF/binding/built-asset regression in `tests/security/bulk-source-authorization.test.ts` and `dotnet/tests/Web.Tests/BulkSourceDisclosureMatrixTests.cs`; execute `npx vitest run tests/security/bulk-source-authorization.test.ts` and `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkSourceDisclosureMatrixTests"` before parity remediation (depends on T100 and T161).
- [ ] T164 [P] Add and run the red-first two-stack SC-002 performance harness in `tests/performance/sc-002-confirmation-latency.mjs` and record discovery, 30,000/100,000 review, polling, authorization-discard, and per-stack confirmation results in `specs/001-sharepoint-bulk-scoring/validation/performance-v3.md`; each Stack B and Stack A sample MUST start one monotonic stopwatch at confirm action, record confirmation acknowledgment and the first successful authorized query of that returned request on the same stopwatch, fail if either exceeds 60 seconds, and execute `node tests/performance/sc-002-confirmation-latency.mjs --stack stack-b` followed by `node tests/performance/sc-002-confirmation-latency.mjs --stack stack-a` before parity remediation (depends on T100 and T161).
- [ ] T165 [P] Add and run red-first FR-010G plus FR-010B/SC-030 cross-stack parity through the same pinned oracle in `tests/integration/bulk-raster-fidelity-parity.test.ts` and `dotnet/tests/Infrastructure.Tests/Rasterization/BulkRasterFidelityParityTests.cs`, recording configured type, PDF magic/signature, structural validity, configured size, positive/exact page count/order, source/output dimensions within ±1 point, SSIM >=0.95, blank/crop verdict, quarantine or removal of invalid output, no original fallback, and zero application/scoring handoff in `specs/001-sharepoint-bulk-scoring/validation/raster-fidelity-parity-v3.md`; run `npx vitest run tests/integration/bulk-raster-fidelity-parity.test.ts` and `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRasterFidelityParityTests"` before parity remediation (depends on T057 and T134).
- [ ] T166 [P] Add and run red-first FR-010C cross-stack retained-content/default-view/authorized-download/page-count/TXT regression in `tests/integration/bulk-content-parity.test.ts` and `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkContentParityTests.cs`; execute `npx vitest run tests/integration/bulk-content-parity.test.ts` and `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkContentParityTests"` before parity remediation, and route failures to T040/T055 or T120/T132 (depends on T057 and T134).
- [ ] T167 [P] Add and run red-first FR-019/SC-031 cross-stack parity for authorized detail-only submit/cancel recorded actor ID plus UTC timestamp and display name resolved solely from that recorded ID, falling back only to the recorded ID for missing user/blank display name and proving username fields are never read or returned, in `tests/integration/bulk-request-actor-parity.test.ts`, `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkRequestActorParityTests.cs`, and `specs/001-sharepoint-bulk-scoring/validation/request-actor-parity-v3.md`; execute `npx vitest run tests/integration/bulk-request-actor-parity.test.ts` and `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRequestActorParityTests"` before parity remediation, and route failures to T074/T079 or T149/T153 (depends on T083 and T157).
- [ ] T168 [P] Add and run red-first FR-020/SC-032 cross-stack parity for first-terminal submitter-only uniqueness, frozen counts, authorized relative link, structural no-item-name DTO, and idempotent acknowledgment in `tests/integration/bulk-terminal-notification-parity.test.ts`, `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkTerminalNotificationParityTests.cs`, and `specs/001-sharepoint-bulk-scoring/validation/terminal-notification-parity-v3.md`; execute `npx vitest run tests/integration/bulk-terminal-notification-parity.test.ts` and `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkTerminalNotificationParityTests"` before parity remediation, and route failures to T075/T080 or T150/T154 (depends on T083 and T157).
- [ ] T169 [P] Add and run red-first FR-010H/SC-015 cross-stack legacy/optional/bulk/pre-change/provider fingerprint parity in `tests/integration/bulk-fingerprint-parity.test.ts` and `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkFingerprintParityTests.cs`; execute `npx vitest run tests/integration/bulk-fingerprint-parity.test.ts` and `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkFingerprintParityTests"` before parity remediation, and route failures to T041/T056 or T121/T133 (depends on T057 and T134).
- [ ] T170 [P] Add and run red-first FR-021 four-way Stack/provider deletion parity, including every original/converted/transient deletion and item/request/content reconciliation row, cleanup-event correlation ID, authenticated deleting-user provenance plus explicit cleanup/reconciliation service actor, UTC timestamp, durable-decision atomicity, update/delete rejection, post-cleanup survival, prohibited-payload scan, and safe-payload parity, in `tests/integration/bulk-job-deletion-parity.test.ts` and `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkJobDeletionParityTests.cs`; execute both named suites before parity remediation and route failures to T087/T096 or T107/T109 (depends on T097 and T111).
- [ ] T171 Implement only the parity corrections exposed by T162–T170 for the complete immutable lifecycle envelope/emitters, actor semantics, state/event or outbox/reconciliation atomicity, safe payloads, FR-010G quarantine boundary, recorded-actor-ID-only projection, and SC-002 durable query timing in `server/services/bulk-audit-writer.ts`, `dotnet/src/Application/BulkScoringRequests/Services/BulkAuditWriter.cs`, `server/workers/bulk-scoring.ts`, `dotnet/src/Application/BulkScoringRequests/Services/BulkItemProcessor.cs`, `server/services/bulk-request-actor-detail.ts`, `dotnet/src/Application/BulkScoringRequests/Queries/BulkScoringQueries.cs`, and `tests/performance/sc-002-confirmation-latency.mjs`, documenting each failing assertion and owning correction in `specs/001-sharepoint-bulk-scoring/validation/parity-remediation-v3.md` (depends on T162–T170).
- [ ] T172 Run every deterministic quickstart scenario after parity remediation and record both-stack results in `specs/001-sharepoint-bulk-scoring/validation/quickstart-v3.md` (depends on T161–T171).
- [ ] T173 Execute PARITY-GATE and record identical contract/security/FR-010G quarantine-removal/content/recorded-actor-only/notification/fingerprint/deletion/SC-002 same-stopwatch evidence plus every expanded Principle I control-plane/request/item/content row, actor, atomicity boundary, prohibited-payload scan, immutability result, and safe fact across both stacks/providers in `specs/001-sharepoint-bulk-scoring/validation/parity-gate-v3.md` (depends on B-GATE T100, A-GATE T161, and T162–T172); validation MUST rerun every command named in T162–T170 after T171, with every command exiting 0.
- [ ] T174 Verify publisher/domain status and the environment/assertion matrix without source-tenant inspection and record sanitized proof in `specs/001-sharepoint-bulk-scoring/validation/publisher-verification-v3.md` (depends on T026, T110, and T161).
- [ ] T175 Capture deployed commercial-cloud/registration proof in `specs/001-sharepoint-bulk-scoring/validation/commercial-cloud-release-v3.md` (depends on connector IaC T088, no-legacy IaC T091, infrastructure validation T097, B-GATE T100, A-GATE T161, and publisher proof T174); run `az cloud show --query "{name:name,aadEndpoint:endpoints.activeDirectory,graphEndpoint:endpoints.microsoftGraphResourceId}" -o json`, `az account show --query "{tenantId:tenantId,environmentName:environmentName}" -o json`, `az ad app show --id $env:MICROSOFT365_SOURCE_CLIENT_ID --query "{appId:appId,signInAudience:signInAudience,publisherDomain:publisherDomain,verifiedPublisher:verifiedPublisher}" -o json`, `az ad app permission list --id $env:MICROSOFT365_SOURCE_CLIENT_ID -o json`, `az ad app show --id $env:MICROSOFT365_SOURCE_CLIENT_ID --query "{web:web.redirectUris,spa:spa.redirectUris,publicClient:publicClient.redirectUris}" -o json`, `az ad app credential list --id $env:MICROSOFT365_SOURCE_CLIENT_ID -o json`, `az ad app federated-credential list --id $env:MICROSOFT365_SOURCE_CLIENT_ID -o json`, and `az rest --method get --url "$($env:MICROSOFT_GRAPH_BASE_URI)/v1.0/servicePrincipals(appId='$($env:MICROSOFT365_SOURCE_CLIENT_ID)')/appRoleAssignments" -o json`; assert commercial cloud/Graph classification, `AzureADMultipleOrgs`, non-null verified publisher/domain, Scope-only permissions, exact environment callbacks, empty SPA/public/password/key/app-role lists, reviewed managed-identity federation only, and sanitized identifiers.
- [ ] T176 Run and retain the exact release-time legacy suites in `specs/001-sharepoint-bulk-scoring/validation/legacy-upload-settings-release-v3.md`: Stack A `npx vitest run tests/integration/application-upload-duplicates.test.ts tests/unit/upload-applications-dialog.test.tsx tests/integration/optional-upload-session.test.ts tests/integration/optional-upload-settings.test.ts tests/unit/optional-upload-settings-ui.test.tsx`; Stack B `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~UploadApplicationsCommandTests|FullyQualifiedName~OptionalUploadSessionTests|FullyQualifiedName~UploadSettingsTests|FullyQualifiedName~OptionalUploadEndpointsTests|FullyQualifiedName~UploadSettingsEndpointsTests|FullyQualifiedName~UploadSettingsPageTests"` (depends on T161 and T169).
- [ ] T177 Verify source authorization material is absent from clients/logs/audit/build outputs, tracked environment files contain no concrete identifiers/secrets, and all no-legacy/no-secret assertions pass in `specs/001-sharepoint-bulk-scoring/validation/final-security-v3.md` (depends on T163, T172, and T175).
- [ ] T178 Issue RELEASE-GATE PASS in `specs/001-sharepoint-bulk-scoring/validation/release-gate-v3.md` only when STACK-B-LIVE-GATE T099, B-GATE T100, STACK-A-LIVE-GATE T160, A-GATE T161, PARITY-GATE T173, publisher proof T174, commercial-cloud proof T175, exact legacy suites T176, and final security T177 pass; release evidence MUST enumerate every expanded audit row and reject any control-plane-only, missing-actor, non-atomic, unsafe-payload, mutable, non-surviving, or Stack/provider-divergent result (depends on T099, T100, T160, T161, and T173–T177).

---

## Expanded Principle I Task Ownership

The following existing IDs are expanded blocker-closure tasks. Their prior scope remains; the
complete lifecycle matrix is additional mandatory work.

| Slice | Red-first tasks | Implementation tasks | Evidence/gate tasks |
| --- | --- | --- | --- |
| Contract/domain | T006, T013, T037 | T020–T021, T051 | T057 |
| Stack B worker/item/content | T039–T041, T058–T062 | T054–T056, T063–T068 | T069 |
| Stack B terminal/retry/count | T070–T075 | T076–T080 | T083 |
| Stack B deletion/reconciliation | T087 | T096 | T097–T100 |
| Stack A foundation/item/content | T107, T117, T119–T121 | T109, T127, T131–T133 | T134 |
| Stack A worker/terminal/retry/count | T136–T138, T146–T150 | T139–T142, T151–T154 | T144, T157, T159–T161 |
| Four-way parity/release | T162 | T171 | T172–T173, T178 |

Every red-first row must be observed failing for the intended missing lifecycle event, actor,
atomicity, immutability, payload-safety, or provider behavior before its implementation task.
Evidence cannot aggregate away a missing typed event.

---

## Dependencies and Execution Order

### Gate Summary

1. **SETUP-GATE**: T001–T010.
2. **FOUNDATION-GATE**: T011–T028; depends on SETUP-GATE, explicitly depends on T018/T026, and
   runs `Microsoft365PublisherGateTests`.
3. **US1-B-GATE (MVP)**: T029–T057; depends on FOUNDATION-GATE.
4. **US2-B-GATE**: T058–T069; depends on US1-B-GATE.
5. **US3-B-GATE**: T070–T083; depends on US1-B-GATE and terminal settlement on US2-B-GATE.
6. **Stack B infrastructure/security prerequisite**: T084–T097; T097 explicitly depends on T084/T085
   assertions, T090/T091 removal, T092 configuration, T093 migration, T094 operational docs, T095
   telemetry, and T096 deletion, then validates recursive formatting, `live/shared`, and every
   Stack B Terraform root.
7. **STACK-B-LIVE-GATE**: T099; depends on complete automated expanded-matrix evidence T098.
8. **B-GATE**: T100; depends on STACK-B-LIVE-GATE and blocks all Stack A tasks.
9. **STACK-A-REMOVAL-GATE**: T101–T106; depends on B-GATE T100.
10. **STACK-A-FOUNDATION-GATE**: T107–T112; depends on STACK-A-REMOVAL-GATE.
11. **US1-A-GATE**: T113–T135; depends on STACK-A-FOUNDATION-GATE.
12. **US2-A-GATE**: T136–T145; depends on US1-A-GATE.
13. **US3-A-GATE**: T146–T158; depends on US1-A-GATE and terminal settlement on US2-A-GATE.
14. **STACK-A-LIVE-GATE**: T160; depends on complete automated expanded-matrix evidence T159.
15. **A-GATE**: T161; depends on STACK-A-LIVE-GATE.
16. **PARITY-GATE / RELEASE-GATE**: T173 and T178; parity depends on both aggregate stack gates,
    red-first T162–T170, remediation T171, and quickstart T172; release depends on both live gates,
    both aggregate stack gates, parity, publisher/commercial proof, exact legacy suites, final
    security, and the complete expanded matrix.

### Independent Test Criteria

- **US1 / P1**: In each stack, independently connect delegated SharePoint, work/school OneDrive,
  and another organizational tenant; verify the exact folder; page a frozen draft without further
  discovery; confirm durable work; query the returned request within the same SC-002 stopwatch;
  and prove FR-010G rejects, quarantines/removes, and never scores every invalid converted output.
- **US2 / P2**: In each stack, independently close the browser, restart the host, and revoke access
  during pre-stage, staged, converting, scoring, and completed phases; prove new source reads stop,
  only source-dependent items fail, staged work finishes, authorization is discarded, and no
  fallback identity is selected.
- **US3 / P3**: In each stack, independently reconcile mixed outcomes, cancel and retry eligible
  work, resolve submit/cancel display names only from recorded actor IDs with recorded-ID fallback,
  and create/acknowledge exactly one submitter-only terminal notification with no item names.
- **Principle I / every story and gate**: Exercise every applicable control-plane/request/item/
  content row, prove atomic state/event or durable outbox/reconciliation emission, use the
  authenticated user or explicit service actor as appropriate, scan for prohibited payload data,
  reject update/delete, retain events through cleanup, and compare SQLite/Azure SQL behavior.

### Tests-first ownership

- T011–T018 must fail for intended gaps before T019–T027.
- T029–T042 must fail before their owning T043–T056 implementations.
- T058–T062 must fail before T063–T068.
- T070–T075 must fail before T076–T082.
- T084–T087 must fail on stale infrastructure/runtime behavior before T088–T096.
- After B-GATE, T101 must fail before T102–T105; T107–T108 must fail before T109–T110.
- T113–T121 must fail before T122–T133.
- T136–T138 must fail before T139–T143.
- T146–T150 must fail before T151–T156.
- T162–T170 must run red-first before parity remediation T171; no cross-stack failure may be waived.

### User-story dependency graph

```text
SETUP -> FOUNDATION -> Stack B US1 (MVP)
                              ├──> Stack B US2
                              └──> Stack B US3
Stack B stories + complete expanded audit matrix + infrastructure/operations -> STACK-B-LIVE-GATE -> B-GATE
B-GATE -> Stack A old-mode removal -> Stack A provider/publisher foundation -> Stack A US1
                                                                               ├──> Stack A US2
                                                                               └──> Stack A US3
Stack A stories + complete expanded audit matrix -> STACK-A-LIVE-GATE -> A-GATE
B-GATE + A-GATE + red four-way complete-matrix evidence + parity remediation -> PARITY-GATE
PARITY-GATE + publisher + commercial proof + named legacy suites + both live gates + complete matrix -> RELEASE-GATE
```

## Parallel Execution Examples

- **US1**: after T028, Stack B tests T029–T042 run in parallel and their distinct classifier,
  admission, settings/audit, raster, content, and fingerprint implementations converge at T057;
  only after B-GATE and T112, Stack A tests T113–T121 run in parallel and converge at T134/T135.
- **US2**: after T057, Stack B renewal/lapse/configuration/worker tests T058–T062 run in parallel
  before T063–T068 converge at T069; after Stack A US1 T135, T136–T138 run in parallel before
  T139–T143 converge at T144/T145.
- **US3**: after T057, Stack B query/cancel/retry/UI/provenance/notification tests T070–T075 run in
  parallel, with finalization waiting for US2 before T083; after Stack A US1 T135, T146–T150 run in
  parallel, with cancellation/notification finalization waiting for T139–T143 before T157/T158.
- **Stack A foundation after B-GATE**: T107/T108 run in parallel only after removal gate T106;
  no Stack A removal or runtime task precedes B-GATE T100.
- **Final validation**: after A-GATE T161, T162–T170 run in parallel and record red-first or
  already-green evidence; T171 performs only evidenced remediation, T172 reruns quickstart, and
  PARITY-GATE T173 reruns every named suite while publisher T174 then gates commercial proof T175.

## Implementation Strategy

1. Complete SETUP-GATE and FOUNDATION-GATE.
2. Deliver Stack B US1 through T057 as the MVP; do not claim B-GATE, Stack A readiness, parity, or
   release from this milestone.
3. Complete Stack B US2/US3, assertions, removal/configuration, migration, telemetry, FR-021
   deletion, activated operational docs, shared/all-Stack-B-root infrastructure validation, and
   the distinct live B-GATE.
4. Only after B-GATE, remove Stack A old modes, pass its provider/publisher foundation, and
   implement US1/US2/US3 plus distinct live A-GATE.
5. Run the cross-stack suites red-first, implement only evidenced parity remediation, rerun
   quickstart and PARITY-GATE, then capture publisher/commercial proof, exact legacy suites, final
   security, and RELEASE-GATE.

## Checklist Format Validation

Every actionable task uses `- [ ] Tnnn [P?] [US?] Description with concrete file path`.
`[US1]`, `[US2]`, and `[US3]` appear only in user-story phases. IDs are unique, contiguous, and in
execution order. `[P]` appears only for distinct-file tasks that can run concurrently after their
stated prerequisites. No task is marked complete without current v3.1 evidence.

## Follow-up: Stack B Scoring Finalization Contention

- [X] T179 Add a red-first batching regression in `dotnet/tests/Infrastructure.Tests/SequentialScoringQueueRepositoryTests.cs`; the original three-run finalization was observed making seven saves instead of one.
- [X] T180 Prepare aggregation outside the transaction, combine the ownership fence/terminal application update/lease release, batch result persistence, and add bounded jittered deadlock retries in `dotnet/src/Application/Common/Services/SequentialApplicationScorer.cs`, `ApplicationScoringFinalizer.cs`, `dotnet/src/Infrastructure/Persistence/Repositories/SequentialScoringQueueRepository.cs`, and `dotnet/src/Infrastructure/Persistence/SequentialScoringExecutionStrategy.cs`; document the policy in `CONFIGURATION.md` (depends on T179).
- [X] T181 Validate constant one-save/one-application-write finalization for 1/3/10 runs, unchanged shared aggregation, rollback, ownership loss between retries, cancellation, deadlock exhaustion, and worker/bulk finalization behavior with the focused Application and Infrastructure test projects (depends on T180). Validation: Application filter `SequentialApplicationScorerTests|ScoreApplicationCommandEvidenceParsingTests|RetryDlqItemCommandTests` passed 29/29; Infrastructure filter `SequentialScoringQueueRepositoryTests|SequentialScoringExecutionStrategyTests|SequentialScoringPoolTests|BulkFinalizationTests` passed 112/112. Both project builds and `git diff --check` passed. Deadlocks were fault-injected over SQLite; live SQL Server contention measurements remain operational validation.

## Follow-up: Prompt-Test and Production Duplicate Isolation

- [X] T182 Add red-first regressions in `dotnet/tests/Infrastructure.Tests/DuplicateFingerprintTests.cs` and `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkImportPipelineTests.cs` proving prompt-test files do not block production import, conversion is applied only when requested, and every selected file has a separate production result. All 10 new cases failed for the intended duplicate-scope regression; the 33 existing cases passed.
- [X] T183 Restrict production duplicate checks to applications without `TestRunId` in `dotnet/src/Infrastructure/Persistence/Repositories/ApplicationRepository.cs` and `UploadSessionRepository.cs`; preserve genuine production/request duplicate checks, compatible historical fingerprints, and prompt-test history (depends on T182).
- [X] T184 Run focused duplicate, import, upload, result-query, and finalization regressions; update the duplicate-scope guidance in `data-model.md`, `research.md`, `quickstart.md`, and `INTEGRATION.md` (depends on T183). Infrastructure filter `DuplicateFingerprintTests|BulkImportPipelineTests|ApplicationRepositoryTests|BulkFinalizationTests` passed 52/52; Application filter `UploadApplicationsCommandTests|OptionalUploadSessionTests` passed 21/21. The test builds compiled all affected projects; `git diff --check` passed. Regression coverage uses SQLite and a deterministic rasterizer/scoring result; live SQL Server and production rollout remain operational validation.

## Follow-up: Job Detail Identifiers

- [X] T185 Add red-first coverage in `dotnet/tests/Web.Tests/JobDetailLazyLoadingTests.cs` for jobs sharing a title but having different job codes and full unique record IDs. Both new cases failed because the identifier header was missing.
- [X] T186 Display labeled job code and full unique job ID below the title in `dotnet/src/Web.Client/Pages/JobDetail.razor`, with responsive wrapping in `JobDetail.razor.css`, and document the header in `INTEGRATION.md` (depends on T185). Both new identifier cases and 26 existing job-detail/usability cases passed; all affected projects built. One unrelated pre-existing test, `JobDetailBulkScoringActionTests.The_page_is_unchanged_when_the_capability_check_fails`, still reflects on the removed `_bgRefreshTimer` field and fails independently of the header change; it was left untouched and explicitly excluded from the 28-test green rerun. `git diff --check` passed.

## Follow-up: Bulk Import Deadlocks and Retry Safety

- [X] T187 Add red-first content/handoff replay, count-hot-path, audit-index, and SQL Server concurrency regressions in `dotnet/tests/Infrastructure.Tests/BulkScoring/`, plus pending-API finalization boundary coverage in `dotnet/tests/Application.Tests/SequentialApplicationScorerTests.cs`.
- [X] T188 Fix replay-safe item/content persistence and handoff, acquire ordered request-first SQL mutation locks, and avoid audit-only recounts in `dotnet/src/Infrastructure/Persistence/Repositories/BulkScoringRepository.cs` (depends on T187). Retry injection covers save and commit failures; repeated committed handoffs do not requeue scoring work.
- [X] T189 Add the audit subject/event lookup index to the EF model, migration, and `server/storage/schema-bulk-v3-upgrades.sql`; document rollout and run focused SQLite and SQL Server validation (depends on T188). All 321 Infrastructure bulk/sequential tests and 169 Application bulk/sequential/scorer tests passed. The Infrastructure run includes five opt-in real SQL Server cases: one-request and multi-request concurrency, fingerprint contention, idempotent script/migration rollout, and successful concurrent heartbeat/row updates while the scoring API response is suspended. No deadlocks were observed. No production database or deployment was changed.

## Follow-up: Stack B Bulk Source Product-Session Recovery

- [X] T190 Add red-first token replay, actionable sign-in, tab-local form restoration, existing-draft recovery, and job return regressions in `dotnet/tests/Web.Tests/EntraTokenRefreshTests.cs`, `BulkScoringSourceConnectionUxTests.cs`, and `JobDetailBulkScoringActionTests.cs`. The initial focused run compiled and reported 17 intended failures and five passing negative cases.
- [X] T191 Implement explicitly opted-in pre-endpoint stale-token replay with preserved JSON, interactive product sign-in, and same-job source dialog restoration in `dotnet/src/Web.Client/Services/StaleTokenRetryHandler.cs`, `ApiClient.cs`, `Components/BulkScoringRequestDialog.razor`, and `Pages/JobDetail.razor`; keep existing drafts read-only during restoration and retain server authorization/freshness checks (depends on T190).
- [X] T192 Run focused transport, source-dialog, job-return, and authentication regressions, build the affected client/server, and document recovery in `AUTHENTICATION.md` (depends on T191). All 95 selected tests passed across `EntraTokenRefreshTests`, `ApiAuthorizationMessageHandlerTests`, `BulkScoringSourceConnectionUxTests`, `JobDetailBulkScoringActionTests`, `SourceConnectionEndpointsTests`, `EntraAuthEndpointsTests`, `JobDetailLazyLoadingTests`, `LoginTests`, and `SimpleAuthModeTests`. The previously documented `_bgRefreshTimer` reflection failure in `JobDetailBulkScoringActionTests.The_page_is_unchanged_when_the_capability_check_fails` was explicitly excluded and left unchanged. The Release server/client build passed with existing dependency-advisory warnings; scoped whitespace checks passed while preserving existing Markdown hard breaks. Tests cover fresh bearer-token reacquisition, one connection creation, interactive renewal, review-choice preservation, authorized read-only draft restoration, and no mutation replay on transport failures, timeouts, or other HTTP denials. Live Entra sign-in after deployment remains an operational smoke test; no deployment, database, or infrastructure change was performed.

## Follow-up: Missing Applications at Bulk Scoring Handoff

- [X] T193 Add red-first regressions in `dotnet/tests/Infrastructure.Tests/BulkScoring/BulkImportPipelineTests.cs` for applications deleted before handoff from importing/converting at attempts 1/239, plus negative cases for existing applications in unexpected states. The four missing-application cases reproduced `BulkHandoffPersistenceException`; the three negative cases passed.
- [X] T194 Distinguish missing applications inside the handoff transaction in `dotnet/src/Infrastructure/Persistence/Repositories/BulkScoringRepository.cs` and classify them as terminal `scoring_failed` items in `dotnet/src/Application/BulkScoringRequests/Services/BulkItemProcessor.cs`, preserving rollback, failure audit/count atomicity, lease release, and existing persistence-error handling without recreating deleted applications (depends on T193).
- [X] T195 Document next-claim recovery and the distinction from ordinary import retry limits in `CONFIGURATION.md`; validate the change with targeted tests (depends on T194). Infrastructure import/replay/worker/finalization/audit suites passed 115/115; Application configuration-version/lapse selectors passed 7/7. The test runs built the affected projects, and `git diff --check` passed. Read-only Azure SQL diagnosis confirmed missing linked applications; all reproduction and mutation tests used local SQLite. No production database, deployment, or configuration was changed.

## Follow-up: Self-Contained Stack B Runtime Packaging

- [X] T196 Add red-first packaging regressions in `tests/unit/stack-b-packaging.test.ts` for audited runtime restore, self-contained Linux x64 publish, and runtime-file validation. Two new cases failed while seven existing cases passed; a direct publish also reproduced missing Linux runtime assets.
- [X] T197 Update `infra/scripts/package-stack-b.sh` and `dotnet/src/Web.Server/TalentMatch.Web.Server.csproj` to publish with server-scoped `PackageStackB=true`, preserving the pinned runtime, ordinary framework-dependent builds, and the client's `browser-wasm` target; document runtime update ownership in `CONFIGURATION.md` and `infra/README.md` (depends on T196). A real restore reproduced the invalid Mono Linux runtime-pack request when the RID was passed globally; server-local deployment properties resolved it.
- [X] T198 Build and inspect `artifacts/stack-b.zip`, run focused packaging and server regressions, and verify evaluated project properties (depends on T197). Audited restore and self-contained Release publish succeeded. The ZIP contains both .NET and ASP.NET Core runtime packs at 10.0.13, Linux x64 ELF runtime binaries, and browser WebAssembly assets; it declares bundled frameworks without shared-framework requirements. Packaging tests passed 10/10 and normal framework-dependent health/auth endpoint tests passed 25/25. Shell syntax and whitespace checks passed. Linux execution smoke testing was unavailable because WSL failed to start and Docker's Linux engine was not running. No Azure deployment or configuration change was made.

## Follow-up: Login and Prompt-Test Token Recovery

- [X] T199 Reproduce the raw API-scope error on the deployed login page and add red-first authentication-route, prompt upload/load, and pre-endpoint stale-token replay regressions in `dotnet/tests/Web.Tests/PollingSettingsProviderTests.cs`, `PromptTestRunnerTests.cs`, and `EntraTokenRefreshTests.cs`. The exact scope-only `AccessTokenNotAvailableException` was reproduced locally; new cases failed for pre-login settings requests, missing interactive recovery, and non-retryable prompt creation.
- [X] T200 Gate protected polling settings on application routes, share explicit product sign-in recovery, handle prompt-test session failures during load/upload/actions/polling, and opt prompt creation into one canonical pre-endpoint stale-token retry in `dotnet/src/Web.Client/Components/PollingSettingsProvider.razor`, `PromptTestRunner.razor`, `Services/ProductSessionRecovery.cs`, and `Services/ApiClient.cs` (depends on T199). Preserve the current job return URL, require file reselection after interactive sign-in, never automatically replay an unknown creation outcome, and direct already-created runs to history.
- [X] T201 Validate and document session behavior in `AUTHENTICATION.md` (depends on T200). All 92 selected UI/authentication/transport tests passed, including same-body retry, no replay after transport failures or other denials, and accepted-create/failing-history recovery. The Release server/client build passed with 10 warnings and no errors. An isolated local Entra-mode browser check with synthetic configuration and temporary SQLite showed the Microsoft sign-in page without the scope-error banner; the helper was stopped afterward. Whitespace checks passed. No Entra/Azure settings or deployment were changed; the existing deployment ZIP predates this follow-up and must be rebuilt.

## Follow-up: Sequential Scoring Lease Reliability

- [X] T202 Reproduce ungated startup/recovery, stalled heartbeat attempts, unbounded retry sets, and unconditional delegated-v3 upgrade writes/rebuilds in `dotnet/tests/Infrastructure.Tests/SequentialScoringPoolTests.cs` and `BulkScoring/BulkSchemaFileTests.cs`. Nine new cases failed for the intended missing behavior. Read-only production evidence confirmed ten fresh lease failures, SQL timeouts, and container replacement with five seconds' termination grace; it did not establish why the platform replaced the container.
- [X] T203 Register the initialization gate independently of connector activation, hold sequential claims/recovery until initialization succeeds, and stop the host without opening the gate on initialization failure in `dotnet/src/Infrastructure/DependencyInjection.cs`, `HostedServices/SequentialScoringPool.cs`, and `dotnet/src/Web.Server/Program.cs`; propagate the claim's persisted lease deadline, bound heartbeat attempts/retry sets, and preserve ownership fences and explicit unknown-outcome retries. Make `server/storage/schema-bulk-v3-upgrades.sql` preserve current constraints and data instead of repeating legacy backfills (depends on T202).
- [X] T204 Add delayed-claim and real SQL Server no-write/no-DDL replay regressions; document startup, renewal budgets, per-instance concurrency, Always On, draining, and provider idempotency requirements in `CONFIGURATION.md` (depends on T203). The focused Infrastructure filter `SequentialScoringPoolTests|SequentialScoringQueueRepositoryTests|SequentialScoringExecutionStrategyTests|BulkSchemaFileTests|DatabaseInitializationGateTests|BulkScoringSqlServerConcurrencyTests` passed 131/131 with no skips across SQLite and a newly created isolated LocalDB instance, which was stopped and deleted afterward. Application filter `SequentialApplicationScorerTests|RetryDlqItemCommandTests|ScoreApplicationCommandEvidenceParsingTests` passed 30/30. The Release server/client build passed with ten existing warnings and no errors; whitespace checks passed. No production data, application settings, deployment, or infrastructure was changed. Deployment and a controlled live bulk-run smoke test remain operational validation.

## Follow-up: SQL Deployment Profile Handoff

- [X] T205 Add red-first deployment regressions in `infra/scripts/tests/sql-profile-deploy.sh` for saving created/reused SQL coordinates, repeated apply, profile preservation, read-only plans, and missing/invalid outputs. The initial run reproduced the absent `SQL_RG` after a successful mocked shared apply.
- [X] T206 Persist authoritative SQL resource coordinates in the selected environment profile immediately after successful shared infrastructure apply in `infra/scripts/deploy.sh`, without changing SQL ownership/reuse flags (depends on T205). SQL resource-group resolution uses the server resource ID rather than the application resource group; profile replacement is atomic and leaves plans read-only. T209 subsequently adds app-stage backfill for stack-only applies.
- [X] T207 Run focused deployment/profile regressions and shell syntax checks; document the automatic SQL handoff and recovery for previously deployed profiles in `infra/README.md`, `docs/ENTRA_AUTHORIZATION.md`, and `CONFIGURATION.md` (depends on T206). SQL profile, Application Insights deployment, stack-selection deployment, and environment-loader regression scripts passed under Git Bash. Coverage includes all shared deployment targets, created/reused SQL in another resource group, repeated applies, blank/duplicate entries, spaces in paths/database names, CRLF and missing final newlines, permissions, missing/inconsistent outputs, and failed applies. Shell syntax and whitespace checks passed. Azure/Terraform calls were mocked; no customer profile, deployment, or live resource was changed.

## Follow-up: Generated Deployment Settings

- [X] T208 Add focused red-first tests in `tests/unit/deployment-profile.test.ts` for public output backfill, preservation of operator settings, selected/disabled features, callback derivation, SQL compatibility, and atomic profile writes. The initial run failed because the new persistence helper was not implemented; the completed focused suite passes 30 cases.
- [X] T209 Extend deployment profile persistence to generated public identifiers/resource coordinates and deployed callback URLs; provide a read-only resource sync command for existing deployments, preserving Entra registrations and explicit configuration (depends on T208). Shared/app applies use one allowlisted atomic profile writer; standalone sync validates tenant/subscription and reads existing selected workspace outputs only. Customer selected profile-only handling: explicit callbacks are preserved and generated source callbacks emit registration review warnings, without directory changes.
- [X] T210 Run focused profile and shell integration tests, syntax checks, and output-format validation; document generated fields, first-deployment prerequisites, and callback registration review (depends on T209). All 30 profile tests, the mocked state-sync/apply/plan/failure integration script, and the existing Application Insights deployment regression passed. Focused ESLint, Node/Bash syntax, Terraform formatting, initialized shared-root/Entra-module validation, and whitespace checks passed. Output copying is allowlisted and excludes sensitive values; optional outputs absent from old state warn explicitly, while invalid enabled outputs fail. No customer profile, live resource, Terraform state, Entra registration, or consent was changed.

## Follow-up: Administrator Seed Schema Prerequisite

- [X] T211 Add red-first metadata-readiness and actionable missing-table regressions in `tests/unit/entra-admin-seed.test.mjs`. The initial run failed on the missing readiness export.
- [X] T212 Check required application tables before executing seed SQL and fix the deployment runbook order so application ZIP startup/schema initialization precedes administrator seeding (depends on T211). Both data modes check seven required tables using read-only metadata; missing-schema errors explain application initialization and target-database verification without creating tables.
- [X] T213 Validate focused seed tests and script syntax; document missing-table diagnosis, target-database verification, and background initialization readiness (depends on T212). All 11 Node seed tests passed, including metadata read-only checks, missing-table recovery, propagated SQL failures, and existing parameterization/transaction/idempotency assertions. Node syntax, focused ESLint, and whitespace checks passed. The active checklist remained complete (16/16). No customer database, deployment, environment file, or directory assignment was accessed or changed; customer-side initialization and retry remain operational validation.

## Follow-up: Fresh Azure SQL Startup Ordering

- [X] T214 Reproduce actual host startup against isolated SQL Server databases with no application schema or with the schema only; cover repeated startup with existing delegated bulk data. Both new empty-database cases reproduced the customer's exact error 208; existing delegated data replay passed.
- [X] T215 Run shared base schema creation before delegated bulk upgrades while preserving legacy-data guards and current-schema replay behavior in `dotnet/src/Web.Server/Program.cs` (depends on T214). The first corrected host run passed all three fresh/schema-only/current cases on real isolated LocalDB; no customer resources were used.
- [X] T216 Validate fresh/current/legacy SQL initialization, build affected projects, and document customer ZIP rebuild/redeployment and initialization-before-seeding recovery (depends on T215). All 40 focused Infrastructure cases passed without skips, including actual host startup on empty/schema-only databases, completed legacy-history migration, draft/active legacy drain guards, current-data replay, SQL contention/idempotency, SQLite migration checks, and initialization gates. Test builds compiled the Release server/client and dependencies with existing obsolete-API warnings; whitespace checks passed. The unique LocalDB test instance was stopped/deleted afterward. No customer database, Azure resources, environment file, or deployment was changed; the customer must rebuild/redeploy the Stack B ZIP before retrying initialization/admin seed.
