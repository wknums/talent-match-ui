# Phase 0 Research: Delegated Microsoft 365 Bulk Scoring

**Feature**: `001-sharepoint-bulk-scoring`  
**Date**: 2026-10-13  
**Spec**: [spec.md](./spec.md)  
**Status**: Complete planned design — all planning decisions are resolved against constitution
v3.0.0, but this document does not claim the target runtime is active before old-runtime removal.
**Contract Revision**: OpenAPI 3.1.0 planned target with the normative requirement/operation map in
`contracts/openapi.yaml`.

This document supersedes the earlier same-tenant OBO/cross-tenant `Sites.Selected` design. The
approved design is one dynamic, per-user, delegated flow for every organizational tenant. Product
authentication continues to authorize jobs; a separate Microsoft 365 source connection authorizes
only the pasted folder journey.

## Repository facts

- Stack B already has the durable request/item worker, Graph source abstraction, authorization
  envelope, Key Vault key wrapper, and `RequestAuthorizationStore`. These are retained and adapted,
  not replaced.
- The existing authorization store persists an MSAL long-running OBO cache keyed only by request.
  It must become a connector-app authorization-code token cache that supports both a short-lived
  pre-confirmation connection and request-bound authorization.
- `SharePointAccessContext`, `BulkScoringOptions`, Terraform, scripts, documentation, tests, and
  OpenAPI still contain the superseded `same_tenant|cross_tenant`, host/root allow-list, and
  application-only reader paths.
- Stack B is implemented first. Stack A runtime parity remains gated until Stack B passes B-GATE;
  shared TypeScript contracts may be updated before that gate.

### Historical validation records

Files under `validation/` remain immutable, dated evidence of what was tested at the time. They are
not current status declarations. In particular, `live-stack-b.md`, spikes S1/S4, and the OBO parts
of S5 exercised the superseded app-only, `Sites.Selected`, same/cross-tenant, certificate-reader, or
OBO designs. They cannot satisfy B-GATE, A-GATE, parity, or release for this authorization-code
connector. The Stack A/B baselines and S2/S3 raster measurements remain applicable within their
recorded assumptions. Any stale task identifiers or "not yet" wording in those historical records
must not be copied into current plan status.

## R1 — Separate multi-tenant connector application

- **Decision**: Create a dedicated confidential Entra application with
  `signInAudience = AzureADMultipleOrgs`. Use the `/organizations` authority for the interactive
  connection so only work/school accounts can enter. Use the returned tenant ID for subsequent
  tenant-specific silent acquisitions.
- **Rationale**: Product sign-in and source access have different audiences, consent, lifetimes, and
  revocation boundaries. A separate app avoids making the product API multi-tenant and excludes
  personal Microsoft accounts.
- **Alternatives considered**:
  - Reuse the product API app and OBO: rejected because source identity may differ from product
    identity and may be in any organizational tenant.
  - `/common`: rejected because it admits personal Microsoft accounts.
  - One connector per source tenant: rejected because it recreates deployment-time tenant setup.
- **Official grounding**:
  - [Single- and multi-tenant apps](https://learn.microsoft.com/entra/identity-platform/single-and-multi-tenant-apps)
  - [Convert an app to multi-tenant](https://learn.microsoft.com/entra/identity-platform/howto-convert-app-to-be-multi-tenant)

## R2 — Authorization-code flow, PKCE, and state

- **Decision**: The server starts an authorization-code flow against `/organizations` using an
  exact registered Web redirect URI, PKCE `S256`, and a cryptographically random opaque `state`.
  The server stores the verifier and return context; neither the folder URL nor sensitive context is
  embedded in `state`. The callback validates `state`, redeems the code once, validates the returned
  tenant/account, and resumes the same job/folder journey.
- **Rationale**: The server is the confidential client and retains every source credential. PKCE
  protects the code, while server-bound state protects the callback and preserves navigation.
- **Alternatives considered**:
  - SPA token acquisition: rejected because tokens must not enter Blazor/React clients.
  - Plain PKCE or no PKCE: rejected; `S256` is supported and recommended.
  - Folder URL in `state`: rejected as sensitive and replayable.
- **Official grounding**:
  - [OAuth 2.0 authorization-code flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-auth-code-flow)
  - [Redirect URI restrictions](https://learn.microsoft.com/entra/identity-platform/reply-url)

## R3 — Delegated scopes and consent

- **Decision**: Request `openid`, `profile`, `offline_access`, delegated `Files.Read.All`, and
  delegated `Sites.Read.All`. Configure only Graph `Scope` entries, never Graph application `Role`
  entries. Normal connection first uses user consent. When tenant policy requires an administrator,
  return a `source_admin_consent_required` result with a tenant-specific admin-consent URL and
  retain the pending journey so it can resume after approval.
- **Rationale**: The source may be a shared folder the connected user does not own. Read-only
  delegated scopes preserve that user's effective access. `offline_access` allows MSAL to maintain
  delegated access for background work subject to tenant policy and revocation.
- **Alternatives considered**:
  - `Sites.Selected`, app roles, or client-credentials Graph access: rejected; all are standing
    application-only access and violate FR-002F.
  - Write scopes: rejected because the feature never mutates source content.
  - Forced admin consent for all tenants: rejected because eligible tenants can use user consent.
- **Official grounding**:
  - [Scopes and permissions](https://learn.microsoft.com/entra/identity-platform/scopes-oidc)
  - [Requesting permissions and consent](https://learn.microsoft.com/entra/identity-platform/consent-types-developer)
  - [Admin consent endpoint](https://learn.microsoft.com/entra/identity-platform/v2-admin-consent)
  - [Microsoft Graph permissions reference](https://learn.microsoft.com/graph/permissions-reference)

## R4 — Strict URL classification without deployment allow-lists

- **Decision**: Parse and classify URL structure locally before Graph is called:
  - HTTPS only; no credentials, fragments, IP literals, non-default ports, or control characters.
  - Work/school SharePoint/OneDrive host must have a valid `*.sharepoint.com` organizational shape.
  - Reject known personal OneDrive hosts (`1drv.ms`, `onedrive.live.com`) and all non-SharePoint
    hosts.
  - Never fetch the pasted URL. Encode it as a Graph sharing token and resolve it only through
    `graph.microsoft.com`.
  - After authorization, verify the resolved object is a folder, its drive type is organizational,
    and its stable drive/item identity remains under the verified root during enumeration.
- **Rationale**: Structural classification prevents direct SSRF and personal-account links without
  coupling deployments to tenant names. Graph plus the connected user's delegated token is the
  source of truth for access.
- **Alternatives considered**:
  - `SHAREPOINT_ALLOWED_HOSTS`, roots, sites, drives, or folders: rejected because arbitrary
    organizational tenants and folders are required.
  - String-prefix subtree checks: rejected; stable drive/item identity and traversal lineage are
    used.
- **Official grounding**:
  - [Access shared DriveItems](https://learn.microsoft.com/graph/api/shares-get?view=graph-rest-1.0)
  - [List children of a DriveItem](https://learn.microsoft.com/graph/api/driveitem-list-children?view=graph-rest-1.0)
  - [Download DriveItem content](https://learn.microsoft.com/graph/api/driveitem-get-content?view=graph-rest-1.0)

## R5 — Protected connection and request authorization

- **Decision**: Reuse `AuthorizationEnvelope` (AES-256-GCM), the Key Vault wrapping-key adapter,
  `IBulkRequestAuthorizationRepository`, and `RequestAuthorizationStore`, generalized as follows:
  1. A pending connection is keyed by a random connection ID and bound to product actor, job, the
     canonical URL hash, resolved tenant/drive/folder, and callback state. It has a 30-minute sliding
     inactivity expiry and a two-hour absolute expiry.
  2. The encrypted payload is an MSAL confidential-client token-cache partition plus source account
     metadata. Access and refresh tokens are never modeled as domain fields.
  3. Confirm atomically rechecks product/job and exact-folder authorization, captures the current
     scoring configuration version, creates the active request and items, and
     moves/re-encrypts the protected cache under request-associated data
     `bulk-source-request:{requestId}`. The pending record becomes consumed in the same commit.
  4. Workers call MSAL silent acquisition using the tenant-specific authority. A refreshed cache is
     re-encrypted under a fresh data key.
  5. Terminal, cancellation-settled, abandoned, expired, revoked, or unusable authorizations have
     ciphertext and wrapped keys cleared; only non-secret lifecycle facts remain.
- **Rationale**: Existing envelope and Key Vault patterns already meet the storage boundary. MSAL
  owns refresh-token rotation and cache semantics; the application owns binding and retention.
- **Alternatives considered**:
  - Store raw refresh-token columns: rejected; use the supported MSAL cache.
  - Put one Key Vault secret per connection: rejected; excessive churn and wider secret access.
  - Reusable user connection: rejected; authorization is folder-journey/request scoped.
- **Official grounding**:
  - [Acquire and cache tokens with MSAL](https://learn.microsoft.com/entra/msal/msal-acquire-cache-tokens)
  - [MSAL Node cache guidance](https://learn.microsoft.com/entra/msal/javascript/node/caching)

## R6 — Phase-aware lapse, reconnect, and retry

- **Decision**: Treat `interaction_required`, invalid/revoked grant, disabled account, revoked
  consent, tenant/account mismatch, and loss of folder access as unusable for further source reads
  by the active request. Atomically mark the authorization `lapsed`, `revoked`, or `accessLost`,
  prevent every new Graph operation, and clear protected material. Fail only non-terminal items
  whose source bytes have not been securely staged. Items with `SourceStoredAt` set, including
  queued/active conversion and items handed to scoring, continue to a terminal result because those
  phases are source-independent. Preserve completed outcomes and notify the recruiter. Reconnect
  never mutates the old request; it authorizes a new retry request after exact-folder verification.
- **Rationale**: This is constitution v3.0.0's source boundary: revocation controls Microsoft 365
  reads, not already imported product-owned bytes. No substitute identity may widen or prolong
  access, while safely staged work is not discarded unnecessarily.
- **Alternatives considered**:
  - App-only or another user's token: prohibited.
  - Fail converting/scoring items after staging: rejected because those phases do not use source
    authorization and constitution v3.0.0 explicitly allows them to settle.
  - Pause indefinitely and keep retrying: rejected because credentials may be intentionally revoked.

## R7 — Confidential-client credential and infrastructure

- **Decision**: Give the connector app one federated identity credential per hosting stack's managed
  identity. Use the managed identity to mint the client assertion needed by the confidential client.
  Keep the existing Key Vault wrap/unwrap key and `Key Vault Crypto User` assignments. Register
  explicit callback URIs per deployed stack/environment through variables whose values remain in
  untracked environment profiles.
- **Rationale**: No client secret or certificate is stored or rotated by the application.
- **Alternatives considered**:
  - Client secret/certificate: rejected for hosted environments; a test-only override is not part of
    the production contract.
  - Reuse the product app registration: rejected by R1.
- **Validation tasks**:
  - Assert `AzureADMultipleOrgs`, delegated scopes only, no Graph app roles, no connector password
    or certificate, and exact redirect URI wiring.
  - Validate the managed-identity client assertion on authorization-code redemption and later silent
    acquisition in a deployment integration test.

## R8 — Product-environment publisher gate, user consent, and admin fallback

- **Decision**: Publisher gating uses only the product deployment environment and the connector
  registration assertion. Production startup or feature activation requires an explicit
  publisher-verified assertion set to true; absent or false fails closed before a source connection
  is created or any authorization-code, token, Graph, or source work begins. Before verification,
  connector use is allowed only in explicitly controlled Development or QA product deployments and
  only for users permitted by normal product/job authorization. The selected source tenant is never
  inspected, classified, or allow-listed for this gate. After the gate passes, source-tenant consent
  policy remains authoritative: use user consent where permitted and the resumable admin-consent
  path where required.
- **Rationale**: FR-002N and SC-029 distinguish deployment readiness from source authorization.
  Verified publisher is a Production property of the connector registration, not a tenant rollout
  list. Product authorization, tenant consent, commercial-cloud validation, and exact-folder access
  are independent downstream decisions.
- **Alternatives considered**:
  - Restrict unverified use to selected source tenants: rejected because the source tenant must not
    affect rollout gating.
  - Treat verification as advisory in Production: rejected because Production must fail closed.
  - Let a source tenant's admin consent bypass the publisher gate: rejected because consent and
    deployment readiness are independent.
- **Official grounding**:
  - [Publisher verification](https://learn.microsoft.com/entra/identity-platform/publisher-verification-overview)
  - [User and admin consent overview](https://learn.microsoft.com/entra/identity/enterprise-apps/user-admin-consent-overview)

## R9 — Graph permission compatibility validation

- **Decision**: Retain only delegated read scopes. Add a live integration gate that resolves an
  accessible SharePoint URL and an accessible work OneDrive URL through `/shares`, enumerates, and
  downloads content using exactly those scopes. Failure blocks release and triggers documentation/
  permission review; it does not permit a write or application permission to be added silently.
- **Rationale**: Current Graph reference pages are inconsistent about the least-privileged
  permission shown for `/shares`; implementation must prove the approved read-only contract.
- **Status**: This is a release validation task, not an open design decision; the required design
  remains delegated and read-only.

## R10 — Stack order and parity

- **Decision**: Implement Stack B now by adapting established code. Freeze the shared contract and
  TypeScript types first. Stack B must complete a live delegated SharePoint and work/school OneDrive
  journey before B-GATE. Stack A implementation tasks are explicit but remain blocked by B-GATE.
  Stack A first removes every old mode, then implements the same connector flow and protected MSAL
  cache. A separate live delegated Stack A journey runs only after that implementation. Both live
  journeys are required for parity and release.
- **Rationale**: Stack B already contains the feature and reusable security primitives. Proving the
  corrected architecture there minimizes duplicate rework, while a Stack B live result cannot prove
  Stack A's callback, cache, worker, or disclosure behavior.

## R11 — Removal and compatibility

- **Decision**: Delete the reader-app/site-grant path and migrate configuration:
  - remove `sharepoint_reader`, `Sites.Selected`, permitted-root and host allow-list Terraform;
  - remove `grant-sharepoint-site.sh` and app-only helpers;
  - remove same/cross-tenant capability fields and environment variables;
  - add connector client ID, callback URI/path, authority, scopes, and pending-connection limits;
  - rewrite registration/authentication documentation to conform to Constitution v3.0.0;
  - existing active records produced by the superseded access model are not silently upgraded.
    Deployment must drain/cancel them, discard their protected authorization, and then apply the
    schema/config migration.
- **Rationale**: Dual identity modes would retain forbidden fallback paths and ambiguous data.

## R12 — Admission controls before authorization or Graph work

- **Decision**: Enforce, in this order, authenticated product/job authorization, request-shape
  bounds, the maximum of 10 pending connections per actor, connection-start limits
  (10/rolling-minute/actor and 30/rolling-minute/source IP), and callback limit
  (60/rolling-minute/source IP). A callback limit is checked before state lookup or authorization
  code redemption. A rejected operation returns a safe `429` with the applicable stable code and
  performs no code redemption, token acquisition/refresh, Graph request, or source read.
- **Rationale**: Pending state and identity-provider work are finite resources, and a callback code
  must remain unconsumed when admission rejects the attempt.
- **Alternatives considered**:
  - Enforce limits after token acquisition: rejected because it consumes external work and may burn
    a one-time code before a request is admitted.
  - One undifferentiated limiter: rejected because actor abuse, source-IP abuse, callback floods,
    and pending-state exhaustion have distinct safe remediation.

## R13 — Independent discovery and draft review pagination

- **Decision**: Discovery continuation is a command backed by an encrypted traversal cursor.
  Reviewing included draft items is a read-only endpoint with an opaque `reviewCursor`, immutable
  `reviewSnapshotVersion`, deterministic `(relativePath, sourceDriveItemId)` ordering, and stable
  `totalItems` for that snapshot. Paging never reads Graph or advances discovery. A completed
  discovery step transactionally increments the snapshot version and total; clients restart review
  at the first page if they want the new snapshot.
- **Rationale**: One cursor cannot safely mean both "read another page of known items" and "discover
  new source entries." Separate contracts make confirmation scope visible and testable.
- **Alternatives considered**:
  - Return the whole draft: rejected at the 30,000-file default.
  - Let reaching the end of a page trigger discovery: rejected because navigation would mutate the
    accepted set.

## R14 — Byte boundaries and both-stack settings

- **Decision**:
  - Bytes are authoritative. `1 MiB = 1,048,576 bytes`; `1 GiB = 1,073,741,824 bytes`.
  - The unsaved per-file default is 8 MiB; existing saved byte values are not rewritten.
  - The in-flight upload default remains 100 MiB; both settings UIs render these byte values in MiB.
  - `MaxFilesPerRequest` defaults to 30,000 and accepts integers 1–100,000.
  - Environment-owned `MaxTotalBytes` defaults to 50 GiB and accepts
    1 MiB–1 TiB inclusive. Invalid startup configuration fails closed before workers or connection
    endpoints are enabled.
  - A draft freezes per-file bytes, max files, max total bytes, supported types, and discovery guard
    values at first discovery. Include a file only when its size is positive, at or below the
    per-file limit, and adding it keeps both count and cumulative bytes at or below (`<=`) their
    limits. Stop before a next file would exceed either request limit.
  - Converted output is validated as non-empty PDF and `<=` the same frozen per-file byte limit.
- **Rationale**: Explicit inclusive bounds avoid off-by-one behavior across stacks and prevent UI
  unit conversion from changing stored policy.
- **Alternatives considered**:
  - Store MiB decimals: rejected because existing settings and file metadata are bytes.
  - Apply changed settings to active drafts: rejected because confirmation scope would drift.

## R15 — Scoring configuration capture and current-use semantics

- **Decision**: Confirmation atomically stores `CapturedScoringConfigVersionId`. When each item is
  handed to scoring, resolve the job's then-current configuration and atomically store
  `ScoringConfigVersionId` on that item. The current job version is read for monitoring. A stable
  `configChangedSinceAcceptance` notice is true when the current job version or any item's used
  version differs from the captured version. The captured version is provenance, not a forced
  scoring snapshot.
- **Rationale**: This preserves current scoring behavior while making changes visible and auditable.
- **Alternatives considered**:
  - Force every item to the captured version: rejected by FR-008A.
  - Store only a boolean: rejected because reviewers need the captured/current/used evidence behind
    the notice.

## R16 — Commercial-cloud boundary, disclosure matrix, and stable public errors

- **Decision**: Support organizational tenants in Microsoft commercial cloud only. Reject personal
  accounts and national/sovereign/government clouds before Graph/source access, returning
  `source_unsupported_account` or `source_unsupported_cloud`. All connection and authorization
  failures use the exact stable codes in FR-015A and a single RFC 9457-style safe problem shape.
  Every authorized request summary/detail requires `authorizationState`:
  `pending|active|lapsed|revoked|accessLost|discarded|unavailableHistorical`.
- **Disclosure matrix**:

  | Surface | May disclose | Must not disclose |
  | --- | --- | --- |
  | Authorized draft/request DTO and recruiter UI | Folder/item names, verified folder URL, safe source display label, safe lifecycle state, captured/current/used config versions | Tokens, code, verifier, cache/key material, raw identity-provider errors |
  | Unauthorized/forbidden/not-found/error/rate-limit response | Stable error code, generic detail, correlation ID, optional retry delay | Folder/item names, raw/verified URL, source account/tenant IDs, hidden existence, token detail |
  | Logs/traces/metrics/audit | Opaque connection/request IDs, product actor, safe event/code/state, timestamp, correlation ID | Folder/item names, URLs, raw source IDs/email, tokens, codes, verifier, encrypted payload, wrapped key, raw provider response |
  | Administrator-generic surface | Aggregate health/limit counts and safe codes | Source content/location/account identity or authorization material |

- **Rationale**: One matrix prevents a safe DTO field from accidentally becoming safe in logs or
  generic administration. Stable schemas give both clients deterministic recovery without revealing
  hidden resource or account facts.

## R17 — Retained originals, converted-default viewing, and page-count integrity

- **Decision**: For every successfully converted non-TXT item, retain exactly one original and one
  converted content record. Record which version was scored, the original page count, and the
  converted page count. Normal document viewing resolves to the scored version, which is the
  converted version when rasterization succeeds; downloading the original is a separate
  job-authorized operation. TXT and conversion-disabled items have only the original and record
  `ScoredVersion=original`. Conversion fails the item rather than silently falling back when output
  is missing, invalid, oversized, or has a different page count.
- **Rationale**: FR-010C requires both versions, a converted default view, and reviewer access to the
  original; SC-010 makes equal page count measurable. A separate authorized original-download path
  avoids exposing storage locations or changing the default scored evidence.
- **Alternatives considered**:
  - Replace the original with the converted document: rejected because the reviewer must be able to
    download the source original.
  - Default to the original after conversion: rejected because the scored converted version must be
    the normal view.
  - Fall back to original scoring on conversion/page-count failure: rejected because it hides a
    changed processing outcome.

## R18 — Shared raw-byte fingerprint and pre-change compatibility

- **Decision**: The canonical duplicate fingerprint is lowercase 64-character SHA-256 over the
  exact original raw bytes, computed before conversion, text decoding, or normalization. One shared
  byte-oriented implementation and persistence contract serves legacy existing upload, optional
  parallel upload, bulk source import, Stack A, and Stack B. Duplicate lookup uses the canonical
  value at production job scope (`TestRunId IS NULL`) while preserving the established
  `allowDuplicates` behavior. Prompt-test originals must not block production import or conversion;
  production creates its own application/result without reusing or promoting the test application.
  Pre-change fingerprints are normalized or backfilled from retained original bytes; compatible historical
  encodings are dual-read during migration, and records without trustworthy bytes/hash are marked
  unverifiable rather than assigned an invented value.
- **Rationale**: FR-010H/SC-015 require the same file to match regardless of its entry path, stack,
  or creation date. Filename, decoded text, converted PDF bytes, and path-specific string hashing
  are not content identity.
- **Alternatives considered**:
  - Keep one hash algorithm/encoding per path: rejected because equivalent bytes would not match
    across stacks.
  - Hash converted output: rejected because conversion is optional and non-canonical.
  - Rehash missing historical content from metadata: rejected because it creates false identity.

## R19 — Job-lifetime retention, deletion cascade, staged cleanup, and audit survival

- **Decision**: Retain request status, every item outcome, and their safe lifecycle facts for as long
  as the owning job exists. Job deletion invokes an idempotent application use case that first
  fences workers, enumerates the job's request content prefixes, removes retained originals and
  converted/staged content through the content-store abstraction, and deletes request,
  item/content, authorization, and related operational rows through provider transactions. SQLite
  runs with foreign keys enabled and must match Azure SQL cascade semantics. Cleanup has durable
  retry/reconciliation so external blob failure cannot be mistaken for completed deletion.
  Immutable audit events survive job deletion, contain no content or secret material, and do not
  use a cascading job foreign key.
- **Rationale**: Database `ON DELETE CASCADE` cannot delete external blobs, while cascading audit
  rows would violate FR-021. One application-level lifecycle plus provider-specific parity tests
  makes deletion complete and observable without weakening audit retention.
- **Alternatives considered**:
  - Rely only on relational cascades: rejected because staged Blob Storage would remain.
  - Delete audit events with the job: rejected because state-change history must survive.
  - Purge terminal requests early: rejected because outcomes must remain for the job lifetime.

## R20 — Documentation sequencing and final regression test policy

- **Decision**: Feature specification, plan, research, data model, OpenAPI, and quickstart are target
  contract artifacts and remain labeled **planned** until the old runtime is removed. Operational
  current-state documents (`README.md`, `AUTHENTICATION.md`, `INTEGRATION.md`, registration and
  operations guidance) switch to the new current state only in the sequenced removal/activation
  tasks; before then, they must describe the actual runtime or label target material as planned.
  Final cross-stack release-regression suites are exempt from red-first because the owning Stack A
  and Stack B implementation slices, each with their own red-first tests, precede those aggregation
  suites. A final-suite failure routes back to the owning implementation task and blocks its gate.
- **Rationale**: Publishing a target contract early is necessary for coordination, but representing
  it as current operations before forbidden paths are removed is unsafe. Requiring final aggregate
  suites to fail before already-sequenced slices would invert the dependency graph without adding
  test discipline.
- **Alternatives considered**:
  - Rewrite operational docs at contract freeze: rejected because it misstates deployed behavior.
  - Exempt implementation-slice tests from red-first: rejected; only post-slice aggregation suites
    receive the narrow exemption.
  - Accept final regression failures as expected red: rejected; they reopen the owning task and
    block parity/release.

## R21 — Cross-stack raster fidelity and independent acceptance oracle

- **Decision**:
  - Treat page count/order, page geometry, visual similarity, blank rejection, and crop rejection
    as separate predicates. Page count/order are exact. Read source/output PDF page boxes directly
    and compare width and height in PDF points with an inclusive absolute tolerance of one point.
  - Run visual comparisons at the DPI actually used for that conversion (`BULK_RASTER_DPI`,
    including the documented step-down path), never at a test-only DPI.
  - Use one pinned, independent Poppler renderer plus one shared pinned Python
    `skimage.metrics.structural_similarity` oracle from both xUnit and Vitest. The renderer under
    test must not grade its own output. Compare grayscale pages with Wang-compatible parameters
    (`gaussian_weights=true`, `sigma=1.5`, `win_size=11`,
    `use_sample_covariance=false`, `data_range=255`) and require every page, not a document
    average, to satisfy `SSIM >= 0.95`.
  - Reject an output page as blank when its fixed-threshold foreground-ink ratio is below 0.05%
    while the corresponding source is above that floor. Reject crop when the output foreground
    bounding box newly touches the outer 0.5% edge margin or retains less than 90% of the
    normalized source foreground bounding-box area. SSIM, blank, and crop predicates all must pass.
  - Use deterministic synthetic one-page and multi-page fixtures with asymmetric page markers,
    mixed portrait/landscape/custom page sizes, edge content, near-blank-but-valid content, and
    deliberate reordered/blank/cropped negative outputs. Retain per-page JSON evidence with page
    index, source/output dimensions, DPI, SSIM, ink ratios, bounding boxes, tool versions, and
    verdict; retain diff images only for failed synthetic fixtures.
- **Rationale**: SC-030 is a universal per-page condition. Direct point geometry avoids DPI
  rounding, asymmetric markers expose reordering, and one independent oracle gives Stack A and
  Stack B the same definition of SSIM. Separate blank/crop checks prevent a high page-wide SSIM
  from hiding a small but material clipped region.
- **Alternatives considered**:
  - Reuse PDFium/each stack's renderer as its own oracle: rejected as circular self-validation.
  - Use document-average SSIM: rejected because one failed page could be hidden by other pages.
  - Let C# and TypeScript use different SSIM libraries/defaults: rejected because the 0.95 boundary
    would not have one cross-stack meaning.
  - Infer blank/crop only from SSIM: rejected because those are explicit independent requirements.
- **Grounding**:
  - [scikit-image structural similarity](https://scikit-image.org/docs/stable/api/skimage.metrics.html#skimage.metrics.structural_similarity)
  - [Poppler `pdftoppm`](https://poppler.freedesktop.org/)

## R22 — Submit/cancel actor provenance and display fallback

- **Decision**: Persist `OwnerActorId` and `ConfirmedAt` as the accepted request's submit actor/time,
  and persist `CancelledByActorId` plus `CancelRequestedAt` atomically on the first cancellation
  request. Actor identifiers are durable provenance; mutable display names are not persisted.
  Authorized request-detail projection batches user lookups and resolves
  `displayName -> recorded actor identifier`. A missing user record, blank display name, or
  unavailable user repository therefore returns the recorded identifier. Usernames are never a
  fallback. Request lists,
  notification payloads, unauthorized/not-found responses, audit payloads, and generic
  administrator surfaces do not expose these detail-only display fields.
- **Rationale**: Recorded identifiers and timestamps satisfy immutable audit provenance even when a
  user is renamed or removed. Read-time resolution gives current friendly names while the explicit
  identifier fallback makes SC-031 deterministic.
- **Alternatives considered**:
  - Persist display names: rejected because they become stale and are not reliable identity.
  - Return an empty/“deleted user” label: rejected because FR-019 requires the recorded identifier.
  - Put actor/time on summaries or notifications: rejected because FR-019 limits them to authorized
    request detail.

## R23 — Terminal notification, counts, link, and acknowledgment identity

- **Decision**: Do not add a notification/outbox table for this in-product-only requirement. The
  request row is the durable notification record:
  - the one-way first terminal transition atomically writes `Outcome`, reconciled/frozen
    completed/failed/skipped/cancelled counters, and `TerminalAt`;
  - notification queries select only terminal rows where
    `OwnerActorId == authenticated actor` and `AcknowledgedAt IS NULL`;
  - the notification DTO contains only `requestId`, `recipientActorId`, terminal outcome, the four
    terminal counts, `terminalAt`, and a server-generated relative request-detail link. It has no
    item-name, item-list, folder, URL, or arbitrary-link field, and dereferencing the link runs the
    normal job authorization again;
  - acknowledgment uses a single conditional update
    `WHERE Id = requestId AND OwnerActorId = actor AND AcknowledgedAt IS NULL`, setting
    `AcknowledgedAt` and `AcknowledgedByActorId` together. Zero rows because it was already
    acknowledged is a successful idempotent no-op; zero rows because the request is absent or not
    owned remains the same non-disclosing not-found result. Repeats cannot change the retained
    acknowledgment or recreate a notification.
- **Rationale**: There is no external delivery boundary. Deriving one notification from the
  request's unique terminal transition eliminates dual-write drift, makes the authoritative item
  counters the notification counters, and naturally enforces one notification per request.
- **Alternatives considered**:
  - Separate notification and outbox tables: rejected as duplicate state without an email/webhook
    dispatcher. Reconsider only if an external channel is later required.
  - Build links from client input: rejected because links must be server-generated and
    authorization-enforcing.
  - Filter item names at serialization time: rejected; a dedicated DTO with no such field gives a
    structural no-name guarantee.

## R24 — Executable publisher/commercial-cloud proof and infrastructure ordering

- **Decision**:
  - FOUNDATION-GATE runs the Stack B publisher matrix directly with
    `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~Microsoft365PublisherGateTests"`.
    Its aggregate filter must include `Microsoft365PublisherGate`; a generic options test does not
    count as publisher-gate evidence.
  - Before release, capture sanitized command output for `az cloud show`, `az account show`,
    `az ad app show`, `az ad app permission list`, Graph service-principal app-role assignments,
    Web redirect URIs, application credentials, and federated credentials. Require `AzureCloud`,
    Microsoft commercial endpoints, `AzureADMultipleOrgs`, non-null verified publisher/domain,
    only delegated Graph `Scope` entries, no app-role assignments, exact environment-provided Web
    callbacks, no password/key credentials, and only reviewed managed-identity federation.
  - Run Terraform format/validate and JSON-plan policy assertions only after migration
    inventory/drain/discard, safe telemetry/audit/alerts, and FR-021 database/content deletion
    cleanup are green. That combined evidence is a B-GATE dependency, followed by the distinct
    Stack A, parity, and release gates.
  - Evidence stores field names, boolean assertions, scope/app-role types, callback count/host
    classifications, tool versions, timestamps, and redacted hashes where correlation is needed.
    It never stores access tokens, credential material, tenant/subscription IDs, object IDs,
    concrete resource names, callback hosts, or managed-identity subjects in checked-in files.
- **Rationale**: Terraform proves intent; live Graph/CLI queries prove deployed registration state.
  Explicit dependencies prevent a green plan validation from masking incomplete migration,
  observability, or deletion behavior. Sanitization satisfies the constitution's environment-value
  restrictions.
- **Alternatives considered**:
  - Treat `terraform validate` alone as infrastructure proof: rejected because it does not inspect
    deployed publisher status or permissions and says nothing about migration/telemetry/deletion.
  - Use the source tenant as rollout evidence: rejected by FR-002N/SC-029.
  - Check in raw CLI JSON: rejected because it contains environment-specific identifiers.
- **Official grounding**:
  - [Publisher verification overview](https://learn.microsoft.com/entra/identity-platform/publisher-verification-overview)
  - [Microsoft Graph application resource](https://learn.microsoft.com/graph/api/resources/application)
  - [Microsoft Graph national cloud deployments](https://learn.microsoft.com/graph/deployments)
  - [Workload identity federation commands](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-create-trust)

## R25 — Named release-time legacy upload/settings regressions

- **Decision**: Release evidence executes and names the existing Stack A suites
  `application-upload-duplicates`, `upload-applications-dialog`, `optional-upload-session`,
  `optional-upload-settings`, and `optional-upload-settings-ui`, plus the Stack B
  `UploadApplicationsCommandTests`, `OptionalUploadSessionTests`, `UploadSettingsTests`,
  `OptionalUploadEndpointsTests`, `UploadSettingsEndpointsTests`, and
  `UploadSettingsPageTests`. These run with the shared fingerprint matrix and both-stack MiB
  settings assertions.
- **Rationale**: Explicit filenames prevent “legacy regressions passed” from becoming an
  unverifiable release claim and protect existing/optional uploads while bulk import changes the
  shared fingerprint and settings presentation.
- **Alternatives considered**:
  - Rely only on new bulk suites: rejected because FR-010/FR-010H require established upload
    behavior to remain compatible.

## R26 — Principle I immutable audit contract

- **Decision**: Use one typed append-only audit envelope for every feature-owned state-changing
  operation, not only its control plane.
  - Preserve the existing source connection create/complete/fail/expire/consume; preview draft
    create/discover/abandon; confirmation; cancellation request; notification create/acknowledge;
    authorization establish/use/lapse/discard; settings change; retry-request creation; and
    job-cleanup pending/completed literals.
  - Add closed literals for discovered-item creation; item claim; import start; source-metadata
    recheck success/failure; original and converted content staging; fingerprint recording;
    duplicate claim/skip; conversion start/complete/fail; invalid converted-output quarantine and
    removal; application/document creation; scoring handoff queued; scoring started when this
    feature owns that transition; item complete/fail/skip/cancel; retry-item creation; request-count
    reconciliation; request terminal outcome finalization; original/converted/transient staged
    content deletion; and item/request/content cleanup or reconciliation corrections.
  - If one state transaction performs multiple typed mutations (for example conversion failure also
    terminally fails an item), it emits every applicable event in that transaction; a broad
    aggregate event cannot substitute for a required row.
  - Every event requires correlation ID, UTC timestamp, opaque subject references, schema version,
    and versioned safe facts. User actions use the authenticated product user as
    `actorType=user`; worker, timer, cleanup, and reconciliation actions use an explicit stable
    non-secret `actorType=service` actor. An optional initiating-user ID may preserve provenance for
    background work but never replaces the service actor that made the mutation.
  - A relational state mutation and its audit insert commit atomically in the same SQLite/Azure SQL
    transaction and both roll back if either fails. Queue delivery first commits a durable
    handoff/outbox decision and event. External content quarantine/deletion commits an idempotent
    durable decision/outcome or reconciliation correction and event because external I/O cannot
    share the database transaction.
  - Audit rows remain outside product/job/request/content/authorization cascades and cannot be
    updated or deleted by product operations. Safe facts may contain only safe states/reasons,
    counts/bytes, configuration versions, content-kind enums, and opaque IDs. They never contain
    codes, tokens, verifiers, caches, keys, raw URLs, source/folder/item names, source
    identity/email, fingerprints, item content, provider payload/error text, or environment
    identifiers.
  - Story gates prove their applicable rows. Distinct Stack B and Stack A live gates, B-GATE,
    A-GATE, PARITY-GATE, and RELEASE-GATE require the complete expanded matrix. PARITY-GATE replays
    it across Stack A and Stack B on SQLite and Azure SQL.
- **Rationale**: Constitution Principle I applies to every mutation, not only request terminal
  state. An explicit matrix prevents “audit covered” from masking a missing transition, field,
  actor kind, atomicity boundary, provider, or stack, while detached immutable retention preserves
  evidence after cleanup. A service actor for background work records who actually performed the
  mutation instead of misattributing it to the request owner.
- **Alternatives considered**:
  - Rely on application logs/telemetry: rejected because they are mutable, retention-variable, and
    not a ledger.
  - Audit only public endpoints: rejected because expiry, authorization use/lapse/discard,
    item claims/import, conversion, scoring handoff, terminalization, notification creation,
    content cleanup, and reconciliation are worker/timer transitions.
  - Keep only one lifecycle summary event per item/request: rejected because it leaves intermediate
    content, duplicate, conversion, handoff, counter, and cleanup mutations silent.
  - Attribute every event to the request owner: rejected because a background mutation requires an
    explicit worker/service actor; the owner may only be retained as initiating provenance.
  - Store request payloads/provider responses for diagnostics: rejected by FR-002G/FR-002G1;
    only opaque IDs, safe state/reason codes, counts, and configuration versions are allowed.

## R27 — FR-010G post-conversion admission before scoring

- **Decision**: Treat conversion completion and scoring admission as separate steps. Before a
  converted non-TXT document can set `ConvertedStoredAt`, `ScoredVersion=converted`, or enter the
  scoring queue, both stacks validate the configured output media type, PDF magic/signature,
  structural parse/document validity, positive page set, configured per-file size, exact source/
  output page count and indexed order, and the existing FR-010B geometry/fidelity predicates.
  Invalid output is quarantined or removed, receives a stable safe failure code, and never falls
  back to original scoring. The same fixtures and verdict schema are replayed at PARITY-GATE.
- **Rationale**: A renderer/fidelity check does not prove that the stored handoff is an allowed,
  structurally valid, configured-size document. The explicit admission boundary makes FR-010G
  observable and prevents malformed or mislabeled conversion output from reaching scoring.
- **Alternatives considered**:
  - Trust the converter's declared MIME type: rejected because declaration is not byte evidence.
  - Let the scoring pipeline reject malformed output: rejected because FR-010G is a pre-scoring
    requirement.
  - Run only page-fidelity checks: rejected because they do not replace type, magic, parser, or
    configured-size validation.

## R28 — SC-002 timing and complete Stack B infrastructure gate

- **Decision**: For each stack, start one monotonic timer when the recruiter submits confirmation.
  Within 60 seconds the API must both acknowledge acceptance and return a request ID that succeeds
  on an authorized status query; record acknowledgment and first-query timestamps separately and
  fail on either breach. PARITY-GATE compares the same workload and measurement definition.
  Separately, Stack B infrastructure validation depends explicitly on delegated/no-legacy
  assertion tests, removal, blank variable-based configuration, migration, telemetry, deletion,
  and activated operational documentation, then runs recursive format checks and validates
  `infra/terraform/live/shared` plus every Stack B root (currently
  `infra/terraform/live/stack-b`). Operational docs are a B-GATE prerequisite.
- **Rationale**: An HTTP acknowledgment without a queryable durable request does not satisfy
  SC-002. Shared-only Terraform validation can miss Stack B root errors, while infrastructure
  syntax cannot prove removal, migration, telemetry, cleanup, or operational readiness.
- **Alternatives considered**:
  - Measure only response latency: rejected because the response may race persistence/read models.
  - Validate only `live/shared`: rejected because Stack B composition is a distinct root.
  - Treat documentation as post-gate cleanup: rejected because operators need correct current-state
    instructions before B-GATE declares the stack ready.

## Research conclusion

All technical-context decisions are resolved. The publisher gate depends only on product environment
and connector verification, never source tenant. Separate live delegated Stack B and Stack A
journeys; FR-010B/SC-030 per-page fidelity; FR-010C content/page-count behavior; FR-010H/SC-015
fingerprint compatibility; FR-019/SC-031 detail-only actor/time fallback; FR-020/SC-032
submitter-only terminal notification and acknowledgment; FR-021 provider cleanup/audit retention;
FR-010G pre-scoring converted-output admission; SC-002 acknowledgment-plus-query timing; the
complete Principle I immutable control-plane/request/item/content lifecycle audit matrix with user
or explicit service actors and atomic/outbox/reconciliation boundaries; the complete Stack B
infrastructure/document dependency chain; and executable publisher/commercial-cloud evidence are
explicit gates with deterministic failure behavior. Historical app-only/OBO evidence is retained
only as history. No constitution amendment or unresolved clarification blocks Phase 1 or Phase 2
planning; the planned status remains until old-runtime removal evidence exists.
