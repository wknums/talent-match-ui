# Quickstart: Delegated SharePoint and OneDrive Bulk Scoring

**Feature**: `001-sharepoint-bulk-scoring`  
**Plan**: [plan.md](./plan.md)  
**Contract**: [contracts/openapi.yaml](./contracts/openapi.yaml)  
**Status**: Planned validation procedure until the old runtime is removed and the target connector
is activated. It is not current-state operations guidance.
**Contract Revision**: OpenAPI 3.1.0; validation follows the normative `x-requirement-coverage` and
`x-endpoint-map` entries.

This quickstart validates the approved single access model: every real source read uses the
connected work/school user's delegated authorization. Product sign-in remains separate. There is no
same/cross-tenant mode, app-only reader, `Sites.Selected`, source host/root allow-list, or fallback
identity.

## 1. Local deterministic setup

Use the existing mock Graph source and local wrapping-key adapter. Concrete environment values
belong only in untracked environment profiles.

```text
MICROSOFT365_SOURCE_MODE=mock
PRODUCT_ENVIRONMENT=
MICROSOFT365_SOURCE_PUBLISHER_VERIFIED=
MICROSOFT365_SOURCE_CLIENT_ID=
MICROSOFT365_SOURCE_CALLBACK_PATH=/api/source-connections/callback
MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH=/api/source-connections/admin-consent-callback
MICROSOFT365_SOURCE_AUTHORITY=
MICROSOFT365_SOURCE_SCOPES=openid profile offline_access Files.Read.All Sites.Read.All
BULK_AUTH_KEY_URI=
BULK_AUTH_DEV_KEY_PATH=
BULK_SOURCE_IDLE_TTL_MINUTES=30
BULK_SOURCE_ABSOLUTE_TTL_MINUTES=120
BULK_DISCOVERY_STEP_FILES=1000
BULK_DISCOVERY_STEP_MAX_ENTRIES=10000
BULK_DISCOVERY_STEP_MAX_SECONDS=45
BULK_MAX_TOTAL_BYTES=
BULK_PENDING_CONNECTIONS_PER_ACTOR=10
BULK_CONNECTION_STARTS_PER_ACTOR_PER_MINUTE=10
BULK_CONNECTION_STARTS_PER_IP_PER_MINUTE=30
BULK_CALLBACKS_PER_IP_PER_MINUTE=60
BULK_ADMISSION_HMAC_KEY_URI=
BULK_IMPORT_CONCURRENCY=4
BULK_CONVERT_CONCURRENCY=2
BULK_RASTER_DPI=150
MICROSOFT_GRAPH_BASE_URI=
```

`BULK_MAX_TOTAL_BYTES` defaults to 50 GiB when absent and must validate within 1 MiB–1 TiB.
Production templates leave every concrete URI, identifier, tenant, endpoint, and secret blank; the
numeric policy defaults above are product constants, not environment identifiers. The untracked
authority value must select the Microsoft commercial-cloud `/organizations` endpoint.

The publisher assertion is an environment-owned deployment assertion, not source metadata. In
Production it must be explicitly true before source-connection endpoints activate. In controlled
Development/QA it may be false or absent, but normal product/job authorization remains mandatory.
No source tenant, host, site, drive, folder, or consent result is consulted for this gate.

The mock must model:

- a SharePoint folder and a work/school OneDrive folder;
- a different organizational tenant;
- user-consent success, admin-consent-required, denied consent, state mismatch, callback replay,
  expired code, PKCE failure, personal-account rejection, and non-commercial-cloud rejection;
- exact-folder access allowed/denied and an inaccessible descendant;
- silent token-cache renewal and an interaction-required/revoked lapse;
- 2,500 eligible files, unsupported/oversized/empty entries, duplicate names, and nested folders.

Start Stack B with the existing Web.Server host. The `BulkScoringWorker` starts in-process. App
Service shutdown-continuity acceptance tests require an always-on non-development environment.

## 2. Entra and Terraform deployment setup

Terraform creates or adopts one dedicated source connector application:

- audience `AzureADMultipleOrgs`;
- Web redirect URIs for each deployed Stack B and future Stack A callback;
- delegated Graph scopes only (`Files.Read.All`, `Sites.Read.All`);
- one federated identity credential for each trusted hosting managed identity;
- no Graph application role, `Sites.Selected`, application password, or application certificate;
- existing Key Vault wrap/unwrap key and `Key Vault Crypto User` role for protected caches.

Environment profiles provide only variable values. Checked-in templates leave all concrete IDs,
URIs, resource names, tenant values, and key URIs blank.

Run:

```powershell
terraform -chdir=infra/terraform/live/shared fmt -check
terraform -chdir=infra/terraform/live/shared validate
node infra/terraform/tests/assert-no-graph-app-roles.mjs
```

These early commands are developer preflight only. They do not satisfy the infrastructure gate
until migration inventory/drain/discard, safe telemetry/audit/alerts, and FR-021 deletion/cleanup
have passed. The gate repeats them with JSON-plan assertions after those prerequisites; see
section 15.

Review a no-apply plan and confirm it contains a connector app, Web redirect URIs, delegated scope
declarations, and federated credentials only. It must not contain a source-reader app, Graph app
role assignment, app password/certificate, per-site permission, permitted root, or allow-listed
host.

Validate the product-environment publisher matrix before any source journey:

1. Production + explicit true publisher assertion: startup/feature activation may register source
   connection endpoints; normal product and source authorization still apply.
2. Production + absent or false assertion: fail closed before connection state, code redemption,
   token acquisition/refresh, Graph, or source access.
3. Explicitly controlled Development or QA + absent or false assertion: connector use is allowed
   only for product users already authorized by normal application/job authorization.
4. Repeat cases 1–3 while selecting at least two source tenants. The result must be identical and
   instrumentation must prove the gate never inspected or allow-listed a source tenant.
5. An uncontrolled or unrecognized product environment with an unverified connector remains
   unavailable.

Complete publisher verification and publisher-domain verification before broad Production release.
Source-tenant policies may still require administrator consent after the publisher gate passes; that
consent path neither changes nor bypasses the gate.

FOUNDATION-GATE must execute the publisher decision itself, not infer it from generic options tests:

```powershell
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~Microsoft365PublisherGateTests"
```

The test must cover Production true/false/absent, controlled Development/QA false/absent, unknown
environments, and a static signature/assertion proving the gate has no source-tenant/account/host/
site/drive/folder input.

### Deployed commercial-cloud and registration proof

Run from an authorized release shell whose concrete values come only from an untracked environment
profile. `MICROSOFT_GRAPH_BASE_URI` must be the commercial Graph base URI; do not place its concrete
value in checked-in scripts or evidence.

```powershell
az cloud show --query "{name:name,aadEndpoint:endpoints.activeDirectory,graphEndpoint:endpoints.microsoftGraphResourceId}" -o json
az account show --query "{tenantId:tenantId,environmentName:environmentName}" -o json

az ad app show --id $env:MICROSOFT365_SOURCE_CLIENT_ID `
  --query "{appId:appId,signInAudience:signInAudience,publisherDomain:publisherDomain,verifiedPublisher:verifiedPublisher}" -o json
az ad app permission list --id $env:MICROSOFT365_SOURCE_CLIENT_ID -o json
az ad app show --id $env:MICROSOFT365_SOURCE_CLIENT_ID `
  --query "{web:web.redirectUris,spa:spa.redirectUris,publicClient:publicClient.redirectUris}" -o json
az ad app credential list --id $env:MICROSOFT365_SOURCE_CLIENT_ID -o json
az ad app federated-credential list --id $env:MICROSOFT365_SOURCE_CLIENT_ID -o json

$servicePrincipalPath = "$($env:MICROSOFT_GRAPH_BASE_URI)/v1.0/servicePrincipals(appId='$($env:MICROSOFT365_SOURCE_CLIENT_ID)')/appRoleAssignments"
az rest --method get --url $servicePrincipalPath -o json
```

The release check fails unless:

- cloud name/environment and authority/Graph endpoints classify as Microsoft commercial cloud;
- `signInAudience` is `AzureADMultipleOrgs`, publisher domain is present, and
  `verifiedPublisher` is non-null;
- every requested Graph permission has type `Scope`; no application `Role` or deployed
  service-principal app-role assignment exists;
- Web redirects exactly match the environment-provided Stack B/Stack A callbacks, while SPA/public
  client redirect lists are empty for the connector;
- application password/key credential lists are empty; and
- federated credentials match only the reviewed hosting managed identities and token-exchange
  audience.

Checked-in evidence records only booleans, counts, permission types/safe scope names, callback
classification/count, cloud classification, command/tool versions, and timestamps. Redact or hash
tenant/subscription/app/object IDs, domains, concrete callback hosts, resource names, and federated
subjects; never retain tokens or credential material.

## 3. Product sign-in and source connection separation

1. Sign into the product and open a job the product user can manage.
2. Paste a work/school SharePoint folder URL.
3. The API validates URL structure and returns a source `authorizationUrl`; it does not reuse the
   product token as Graph authority.
4. Sign into Microsoft 365 with a work/school identity. It may differ from the product identity and
   may belong to another organizational tenant.
5. Verify `/organizations`, exact registered redirect URI, PKCE `S256`, and opaque state are present.
   Verify no folder URL or job information is embedded in state.
6. After callback, the browser returns to the same Job Detail journey. No code, access token,
   refresh token, cache blob, verifier, or wrapped key is visible in navigation, page data, logs, or
   network JSON.
7. Repeat with work/school OneDrive and with a different organizational tenant.
8. Personal OneDrive fails with `source_unsupported_account`; unrelated hosts fail with
   `source_folder_invalid_or_unavailable`. Both fail before any Graph call.
9. A national, sovereign, government, or other non-commercial cloud account fails with
   `source_unsupported_cloud` before token acquisition for Graph or source access.

## 4. Admission controls before token and Graph work

1. Create 10 unexpired pending connections for one actor. The next start returns `429`
   `source_connection_limit_reached`.
2. Exceed 10 starts/rolling minute for one actor and 30 starts/rolling minute for one source IP.
   Each rejected start returns `429 source_connection_rate_limited`.
3. Exceed 60 callbacks/rolling minute for one source IP. The rejected callback returns `429`
   `source_callback_rate_limited`.
4. For every rejection, assert no pending state was added (except the already-counted rows), no
   callback state lookup or authorization code redemption occurred, no token was acquired or
   refreshed, and no Graph/source call occurred. Then retry the same unredeemed callback code after
   the rate window only where the identity provider's code lifetime still permits it.
5. Verify limiter storage contains only an HMAC subject and expiry, never a raw source IP, folder,
   source account, authorization code, or token.

## 5. Consent paths

### User consent allowed

1. Use a source tenant that permits user consent.
2. Approve the delegated read scopes.
3. Verify folder access and continue without administrator involvement.

### Administrator consent required

1. Use a source tenant whose policy blocks user consent.
2. Verify the product returns `source_admin_consent_required`, reveals no folder
   name/count/content, and retains only the pending journey.
3. Follow the tenant-specific admin-consent action.
4. An administrator grants tenant consent to the dedicated connector app.
5. Resume the pending journey, rotate state/PKCE, sign in as the source user, and verify the folder.
6. Never trust the `tenant` query value returned by the admin-consent callback; the subsequent token
   and `tid` validation establish tenant identity.

Denied or failed consent must leave the product identity/session unchanged.

## 6. Exact-folder verification, bounded discovery, and independent review

1. After callback, call preview. The server resolves the pasted URL through Graph `/shares` using
   the delegated token and verifies an organizational folder before returning names or counts.
2. Verify included files, total bytes, exclusion counts by reason, and unreadable count. Excluded
   and inaccessible names are absent.
3. Verify traversal stays within the stable drive/folder subtree.
4. Discover the large mock folder:
   - first step includes at most 1,000 eligible files;
   - each step returns within 60 seconds;
   - for Stack B and then Stack A, start one monotonic timer at the confirm action and record the
     confirmation acknowledgment time and the first successful authorized status-query time for
     the returned request ID; both must be at or below 60 seconds on the same run;
   - the response says all other and not-yet-scanned entries are excluded;
   - `discover-more` resumes the encrypted server cursor;
   - confirmation after step one imports only that step's included files;
   - configured file/byte limits end in `limit_reached`, not an error.
5. Page `GET /bulk-scoring-requests/{requestId}/draft-items` using `reviewCursor`. Assert each page
   has a stable `totalItems`, deterministic ordering, and `reviewSnapshotVersion`; no page request
   calls Graph, changes the discovery cursor, adds an item, or changes counts.
6. Explicitly run `discover-more`; assert it commits new items and increments the review snapshot.
   An old review cursor then returns safe `409 draft_review_changed` and the new first page exposes
   the new stable total.
7. Exercise exact byte boundaries:
   - zero-byte and unsupported files are excluded;
   - exactly `MaxBytesPerFile` is eligible and one byte over is excluded;
   - exactly `MaxFilesPerRequest` and `MaxTotalBytes` are allowed;
   - the next file that would exceed either frozen limit is not inserted;
   - invalid bulk total configuration below 1 MiB or above 1 TiB fails startup closed;
   - converted output must be a non-empty valid PDF no larger than the same frozen per-file limit.
8. With deterministic 1-, 30,000-, and 100,000-item drafts, page every item with no omissions or
   duplicates and with zero discovery continuations.
9. Leave the connection idle beyond 30 minutes, and separately beyond two hours absolute. Preview,
   discover-more, and confirm return `source_connection_invalid_or_expired`, while protected
   material is gone.

## 7. Confirmation, configuration versions, and background continuity

1. Confirm with enough files to remain active.
2. In one transaction verify:
   - current product/job authorization;
   - fresh exact-folder access as the connected source user;
   - request/item durability;
   - the current scoring configuration stored as `capturedScoringConfigVersionId`;
   - protected MSAL cache moved from connection scope to request scope and re-encrypted;
   - pending connection consumed and cleared;
   - request activated before `201` returns.
3. Close the browser. Poll from another product session; item counts keep advancing.
4. Stop and restart the API during import. Expired leases recover without duplicate application or
   scoring outcomes.
5. Verify the worker silently renews delegated access from the request cache and rotates the
   encrypted cache. No token appears in logs/traces/audit.
6. Verify a request cache cannot be opened with another request, product user, source tenant/account,
   drive, or folder binding.
7. Change the job configuration after acceptance. Assert each item resolves and records the current
   `scoringConfigVersionId` when handed to scoring, rather than forcing the captured version.
   Monitoring returns captured and current versions and sets `configChangedSinceAcceptance` whenever
   the current or any used version differs.

## 8. Phase-aware lapse, access loss, and reconnect

1. Start a request and trigger each mock condition:
   - interaction required;
   - refresh/consent revoked;
   - source account disabled;
   - source tenant/account mismatch;
   - exact-folder permission removed.
2. Assert every new source read stops. Items without securely staged original bytes fail with
   `authorization_lapsed` or `access_denied`; items with `SourceStoredAt`, including queued/active
   conversion and scoring, continue to their own terminal outcome without Graph. Completed items
   remain unchanged and the recruiter is notified.
3. Verify no application token, deployment identity, product token, or other user's authorization is
   attempted.
4. Choose retry. The old authorization remains discarded. A new source connection is required and
   a new request contains only retryable pre-scoring items.
5. Verify terminal/cancel-settled authorizations are discarded within 15 minutes.

## 9. Rasterization, retained originals, duplicates, settings, monitoring, and disclosure

- With rasterization on, every successfully converted non-TXT item retains original and converted
  content, records `scoredVersion=converted`, and opens the converted scored document by default.
  The retained original is downloadable only through the request/item operation after associated-job
  authorization. Unauthorized and missing requests/items/content return the same non-disclosing
  outcome and never expose a blob URI or reusable credential.
- Run the shared synthetic raster-fidelity manifest for one-page and multi-page
  PDF/DOCX/Markdown fixtures, with mixed portrait/landscape/custom dimensions and asymmetric page
  markers. Page count and indexed order must match exactly; source/output PDF page-box width and
  height must differ by no more than one point.
- Render both sides with the pinned independent Poppler oracle at the actual conversion DPI
  (`BULK_RASTER_DPI`, plus the documented step-down path). Every page pair—not a document
  average—must have grayscale SSIM >= 0.95. Each output page must independently pass the
  foreground-ink blank predicate and normalized foreground-bounding-box crop predicate.
- Deliberately reordered, dimension-shifted, low-SSIM, blank, and edge-cropped negative artifacts
  must each fail for the intended predicate. TXT remains original. Corrupt, protected, malformed,
  missing-page, invalid-PDF, oversized, or fidelity-invalid output fails the item without fallback
  to original scoring.
- Before fidelity acceptance or scoring handoff, inject converted outputs with a wrong configured
  media type, wrong PDF magic/signature, truncated/corrupt structure, zero/invalid pages, page-count
  or indexed-order mismatch, and a size exactly at and one byte above the configured per-file
  maximum. Both stacks must accept the exact boundary only when every other predicate passes,
  reject every invalid case with its safe reason, remove/quarantine the temporary output, and prove
  that no application document or scoring message was created.
- Execute both stack entry points against the same oracle and retain per-page synthetic JSON:

  ```powershell
  dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRasterFidelityTests"
  npx vitest run tests/integration/bulk-raster-fidelity-parity.test.ts
  ```
- With rasterization off, no converted content is created.
- Raw-byte SHA-256 uses the exact same lowercase 64-character value across legacy existing upload,
  optional parallel upload, bulk source import, Stack A, and Stack B. Replay the same binary,
  multi-byte text, empty, and large byte fixtures through every path. The first stored copy must be
  detected by every other path when duplicates are disallowed; `allowDuplicates=true` must preserve
  intentional duplicates without changing fingerprint identity.
- Seed compatible pre-change fingerprints in each provider and prove they are still detected.
  Backfill from retained raw originals where possible, normalize compatible historical encodings,
  and mark records without trustworthy content/hash unverifiable rather than inventing identity.
  Conversion output, filename, path, or decoded text must never define the fingerprint.
- Score a folder's files in prompt tests first, then import them for production with duplicates
  disallowed. Every selected unique file must create its own production application and appear in
  production results/counts; prompt-test applications and scores remain unchanged and hidden there.
  Repeat with conversion on and off and with canonical and compatible pre-change fingerprints.
  Conversion must run for eligible production files when selected (plain text still bypasses it),
  rather than reusing unconverted test documents. Genuine production and request-local duplicates
  must still be skipped. Automated Stack B coverage is in `DuplicateFingerprintTests` and
  `BulkImportPipelineTests`.
- Both Stack A and Stack B settings screens display per-file and in-flight byte values as MiB using
  1 MiB = 1,048,576 bytes. The unsaved per-file default is 8 MiB; the in-flight default remains
  100 MiB; saved byte values are unchanged. Maximum files defaults to 30,000 and validates
  1–100,000.
- Request/item tables are paginated, counts reconcile, cancellation lets already-scoring items
  settle, and in-product notifications poll within 30 seconds.
- On authorized request detail, verify the submit actor/time and, after cancellation is requested,
  cancel actor/time. Test an existing user with a display name, a missing user row, and a blank
  display name; the last two return the recorded actor identifier. A username value, if present,
  is never returned as an intermediate fallback.
  Request summaries, notifications, unauthorized/not-found results, audit, and generic admin
  responses contain none of these detail-only friendly actor fields.
- Race at least two finalizers on one request. Exactly one first-terminal transition freezes the
  outcome and completed/failed/skipped/cancelled counts. Only the submitting recruiter sees exactly
  one notification; another recruiter (including one with access to the job) sees none for that
  request. The payload has the frozen counts and a server-generated relative request-detail link,
  has no item-name/list field, and the link re-runs job authorization.
- Acknowledge concurrently and then repeat acknowledgment. Exactly one retained acknowledgment
  actor/time remains, every repeat is a successful no-op, and no call recreates a notification.
  Run the same fixture in both stacks:

  ```powershell
  dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkScoringAuditNotificationTests"
  npx vitest run tests/integration/bulk-scoring-audit-notifications.test.ts
  ```
- Every authorized request summary/detail includes `authorizationState` as one of
  `pending|active|lapsed|revoked|accessLost|discarded|unavailableHistorical`. Historical records
  without trustworthy state use `unavailableHistorical`.
- Contract tests map every FR-015A error code to the common problem shape and verify exact status,
  safe message, correlation ID, and any `Retry-After` header.
- Disclosure tests apply this matrix:

  | Surface | Allowed | Forbidden |
  | --- | --- | --- |
  | Authorized recruiter draft/request DTO/UI | Folder/item names, verified URL, masked source display, safe state/config versions | Code/token/cache/verifier/key/provider error |
  | Unauthorized/error/rate-limit response | Stable code, generic detail, correlation ID, retry delay | Folder/item/URL/source identity, hidden existence, token detail |
  | Logs/traces/metrics/audit | Opaque IDs, user or explicit service actor, safe event/code/state, correlation ID, UTC timestamp | Folder/item/raw URL/raw source identity/email, fingerprint, content, provider payload, and all authorization material |
  | Generic admin UI/API | Aggregate health/limit counts, safe codes | Source location/content/account identity and authorization material |
  | Submitter notification | Terminal outcome, four counts, terminal time, relative authorized detail link | Item names/list, folder/source data, arbitrary link, submit/cancel display fields |

### Principle I immutable audit matrix validation

Replay all of these transitions in Stack B and Stack A against SQLite and Azure SQL:

| Operation rows that must exist | Required proof on every row |
| --- | --- |
| Source connection create, complete, fail, expire, consume | Correlation ID, user actor for explicit action or timer service actor for expiry, UTC timestamp, immutable survival after connection/protected-data cleanup, safe payload, four-way parity |
| Preview draft create, discover, abandon | Same proof; discovery facts contain only safe counts/bytes/codes and no names |
| Discovered item create; item claim, lease renew/recover/release, and import start | Same proof; every mutation has an explicit discovery/import worker service actor and opaque subject |
| Source metadata recheck success and failure | Same proof; safe outcome/reason only, with no ETag, URL, path, filename, provider payload, or content |
| Original/converted content stage and fingerprint record | Same proof; content kind/count/bytes may be safe facts, but no content, name, location, or fingerprint value |
| Duplicate claim and duplicate skip | Same proof; no fingerprint, item name, or existing-document identity leaks |
| Conversion start, complete, and fail | Same proof; safe validation/reason codes and numeric page/size results only |
| Invalid converted output quarantine and removal | Same proof using durable external-content decision/outcome; no location, content, or provider error |
| Application/document create and scoring handoff queued | Same proof; handoff event is atomic with durable outbox/queue state and precedes delivery |
| Scoring started, only when this feature owns that transition | Same proof; absence is accepted only with documented ownership by the downstream scoring subsystem |
| Item complete, fail, skip, and cancel | Same proof; if another mutation occurs in the same transaction, every applicable typed event exists |
| Retry request and retry item create | Same proof; links contain opaque request/item IDs only |
| Request counts reconcile and request terminal outcome finalize | Same proof; reconciled/frozen counts and safe outcome commit atomically with every applicable event |
| Notification create and acknowledge | Same proof; first-terminal and first-ack actor/time remain immutable under races/repeats |
| Authorization establish, use, lapse, discard | Same proof; no provider/token/cache/account data and audit survives ciphertext removal |
| Original, converted, and transient staged-content delete | Same proof through durable idempotent content-deletion outcomes |
| Item/request reconciliation correction and content-cleanup reconciliation pending/completed | Same proof; before/after states are safe enums/counts only and repeated reconciliation is idempotent |
| Settings mutation | Same proof; administrator actor and validated numeric before/after values only |
| Job deletion cleanup/pending reconciliation | Same proof; ledger survives every relational/content cascade and partial-cleanup retry |

For user actions require `actorType=user` and the authenticated product user ID. For worker, timer,
cleanup, and reconciliation transitions require `actorType=service` and the stable non-secret
service actor ID; an optional initiating-user ID never substitutes for the service actor. Inject an
audit-write failure into each relational mutation family and verify both state and event roll back.
For scoring queue and external content boundaries, verify the durable outbox/decision/reconciliation
state and event are atomic, then verify idempotent delivery/I/O outcomes. Attempt ledger update/
delete through every product and provider path and verify it is impossible.

Compare event type, correlation continuity, actor type/ID, initiating-user semantics, UTC timestamp,
schema version, and safe facts byte-for-byte after normalizing provider timestamp precision. Scan
serialized events and evidence for tokens, codes, verifiers, caches, keys, raw URLs, folder/item
names, source identities/emails, fingerprints, item content, provider payload/error text, and
environment identifiers. A missing event/field, unsafe fact, wrong actor, non-atomic mutation,
mutable/deleted row, or stack/provider mismatch fails the owning story gate. The distinct
STACK-B-LIVE-GATE, B-GATE, STACK-A-LIVE-GATE, A-GATE, PARITY-GATE, and RELEASE-GATE must each
require the complete expanded matrix rather than accepting the former control-plane subset or
aggregate event counts.

## 10. Job-lifetime retention and deletion cleanup

Run the same suite against SQLite with `PRAGMA foreign_keys=ON` and Azure SQL:

1. Create draft, active, completed, failed, cancelled, and retry requests. Verify request status,
   every item outcome/reason, captured/used scoring versions, and safe lifecycle facts remain
   queryable for as long as the job exists; no terminal-age cleanup removes them.
2. Stage originals, converted content, and a transient partial object under multiple request
   prefixes. Delete the associated job through the authorized product operation while a worker is
   eligible to claim work.
3. Verify worker fencing prevents new claims or content recreation. Verify job/request/item/content/
   authorization/application-document operational rows follow the designed cascade in both
   providers and every staged content kind is deleted through the content-store abstraction.
4. Verify immutable audit events for request/item state changes survive job deletion, contain only
   opaque IDs and safe facts, and contain no source URL/name/content, account identity, fingerprint,
   or authorization material.
5. Inject a partial blob-cleanup failure. The operation must not report completed cleanup while
   known staged content remains; durable retry/reconciliation removes the orphan. Repeating job
   deletion and cleanup is idempotent.
6. Compare provider snapshots and require identical observable retention/deletion results.

## 11. Migration from the superseded reader model

Before deploying the connector flow:

1. Inventory old-model drafts/active requests.
2. Drain or cancel them; discard old protected authorization. Do not translate it.
3. Apply the EF migration and Stack A schema parity update.
4. Remove configuration values for source tenant/client secret/certificate, permitted roots,
   allowed hosts, same/cross-tenant access mode, and reader app.
5. Destroy the superseded reader application and any site grants through an explicitly reviewed
   migration step.
6. Delete `infra/scripts/grant-sharepoint-site.sh` and old app-only helper paths.
7. Deploy the source connector app configuration and callback URIs.
8. Verify old completed request history remains readable and no active request can use the old path.
9. Treat `validation/live-stack-b.md` and spikes S1/S4/S5 as historical records of the superseded
   modes only; do not use their PASS or pending language as current gate evidence.
10. Keep this feature's spec/plan/research/data-model/OpenAPI/quickstart labeled planned during early
    contract work. Rewrite `README.md`, `AUTHENTICATION.md`, `INTEGRATION.md`, registration, and
    operational guidance to the new current state only in the sequenced removal/activation tasks.
    Until then, those operational documents describe the runtime that actually exists or label
    target sections as planned.

Rollback restores application/schema binaries only. It cannot restore discarded credentials and
must never restore app-only fallback behavior.

## 12. Stack B automated validation (pre-gate)

Run focused tests, then full tests and build:

```powershell
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkScoring|FullyQualifiedName~SharePoint|FullyQualifiedName~SourceConnection|FullyQualifiedName~Authorization"
dotnet test dotnet/TalentMatch.slnx
dotnet build dotnet/TalentMatch.slnx --configuration Release
```

Required focused coverage:

- URL structural classifier and no outbound call on rejection;
- state/PKCE/callback replay and expiry;
- product/source identity separation and cross-tenant organizational accounts;
- user consent and admin-consent-required resume;
- envelope associated-data binding and Key Vault wrapper;
- pending-to-request atomic move, silent cache rotation, lapse/discard;
- no DTO/log/audit secret leakage;
- exact-folder traversal, Graph throttling, pagination, import idempotency;
- FR-010B page count/order, ±1-point dimensions, every-page SSIM/blank/crop rejection through the
  shared independent oracle;
- FR-019 detail-only submit/cancel actor-time and display-name/actor-ID-only fallback;
- FR-010G converted output type/magic/document/page/size admission before scoring;
- all applicable Principle I immutable audit-matrix rows;
- FR-020 one submitter-only terminal notification, frozen counts, authorized relative link,
  structural no-item-name DTO, and retained idempotent acknowledgment;
- EF migration from completed old-model history and refusal while old active work exists;
- Blazor Job Detail navigation, callback-resume banner/dialog, reconnect/retry, and WCAG states;
- Terraform delegated-only/no-secret/no-app-role assertions.

These checks are necessary but do not pass B-GATE without the live journey below.

## 13. Stack B live delegated journey and B-GATE

After Stack B implementation, in controlled commercial-cloud tenants and through the Stack B
browser/API/worker path, use exactly the approved delegated scopes:

1. Connect a SharePoint folder and a work/school OneDrive folder.
2. Resolve each with `/shares`, enumerate children, and download one allowed file.
3. Repeat from a different organizational tenant.
4. Confirm a shared folder the connected user can access but does not own.
5. Remove access and verify the request stops.
6. Record consent/Conditional Access behavior without recording tokens or source content.
7. Close the browser after acceptance and verify background import/scoring continues.
8. Exercise every expanded Principle I request/item/content mutation that is applicable to the
   live journey, including duplicate, conversion failure/quarantine/removal, terminalization,
   staged-content cleanup, and reconciliation paths; join the sanitized live record to automated
   provider evidence for rows that cannot be safely forced in the commercial tenant.

A failure blocks **STACK-B-LIVE-GATE** and **B-GATE**, and therefore blocks Stack A runtime
implementation. The live gate cannot accept only control-plane rows and does not authorize silently
adding write or application permissions.

## 14. Stack A old-mode removal, implementation, live journey, and A-GATE

Only after B-GATE:

1. Remove Stack A same/cross capability branches, application-only/OBO source readers, source
   tenant/client credential settings, host/root/site/drive allow-lists, grant helpers, fallback
   identity selection, and obsolete tests.
2. Implement the same OpenAPI, schema, admission order, connector app, cache binding, exact-folder
   discovery, independent draft pagination, phase-aware lifecycle, settings MiB behavior, disclosure
   matrix, configuration-version reporting, raster-fidelity validation, actor fallback, and
   terminal notification/acknowledgment semantics.
3. Run:

```powershell
npm run test
npm run build
npm run lint
npm run test:e2e
```

4. After implementation, run a distinct live delegated SharePoint and work/school OneDrive journey
   through Stack A, including another organizational tenant, background continuation, and lapse.
   Exercise and correlate the same complete expanded request/item/content audit matrix as Stack B,
   including safe failure/cleanup/reconciliation paths; use sanitized automated provider evidence
   only for transitions that cannot be safely forced live. Stack B evidence cannot substitute for
   this run. Automated and live results must both pass **STACK-A-LIVE-GATE** and **A-GATE**.

## 15. Parity and broad-production release gate

### Infrastructure prerequisite chain

Infrastructure validation is accepted only after the T084/T085-equivalent assertion tests,
old-mode removal, blank variable-based connector configuration, migration, telemetry, deletion,
and activated operational-document slices are green. Run the migration/telemetry/deletion tests
first and verify `README.md`, `AUTHENTICATION.md`, `INTEGRATION.md`, and registration/operations
guidance no longer present the removed Stack B mode as current:

```powershell
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkSourceOperationsTests"
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkJobDeletionTests"
```

`BulkSourceOperationsTests` must cover migration inventory/refusal/drain/discard, completed-history
readability, and safe telemetry/audit dimensions. `BulkJobDeletionTests` must cover both providers,
worker fencing, every staged content kind, partial-cleanup reconciliation, repeat idempotency, and
audit survival. Only then run and record both Terraform assertions, recursive formatting, and
validation of shared plus every Stack B root (currently `live/stack-b`):

```powershell
terraform fmt -check -recursive infra/terraform
terraform -chdir=infra/terraform/live/shared validate
terraform -chdir=infra/terraform/live/stack-b validate
node infra/terraform/tests/assert-source-connector.mjs
node infra/terraform/tests/assert-no-legacy-sharepoint-access.mjs
git diff --check
```

This combined result is a B-GATE dependency. STACK-B-LIVE-GATE precedes B-GATE;
STACK-A-LIVE-GATE precedes A-GATE; both aggregate stack gates precede PARITY-GATE; all live,
aggregate, and parity gates plus deployed publisher/commercial-cloud proof precede RELEASE-GATE.
Every one of those gates requires the complete expanded Principle I matrix. B-GATE cannot pass if
the operational-document switch is incomplete. Terraform syntax or plan success alone cannot
waive a missing assertion, removal/configuration, migration, telemetry, deletion, audit row, or
documentation prerequisite.

Replay shared contract and Graph fixtures in both stacks. PARITY-GATE requires identical public enum
literals, required authorization state, stable problem codes, state transitions, consent/reconnect,
pagination, limit boundaries, configuration-version behavior, FR-010B per-page fidelity,
FR-010G post-conversion type/magic/document/page/size admission, SC-002 confirmation
acknowledgment-plus-query timing, retained-original/default-view/page-count behavior, FR-019
actor/time fallback, FR-020 terminal
notification/acknowledgment, canonical and pre-change duplicate compatibility, job-lifetime
retention/deletion cleanup, every Principle I audit-matrix row, database semantics, and security
assertions; encrypted payload formats may differ internally.

Foundation, story, migration, provider, and stack-specific implementation tests remain red-first.
The final cross-stack release-regression suites are the narrow exception: they run only after their
owning implementation slices and slice-level red tests. They need not be observed red before
implementation. Any final-suite failure routes back to the owning contract, domain, provider,
worker, UI, security, infrastructure, or documentation task and blocks the applicable gate; it is
never accepted as expected red evidence.

### Named legacy upload/settings release suites

Run these existing suites by name and retain their individual results:

```powershell
npx vitest run tests/integration/application-upload-duplicates.test.ts tests/unit/upload-applications-dialog.test.tsx tests/integration/optional-upload-session.test.ts tests/integration/optional-upload-settings.test.ts tests/unit/optional-upload-settings-ui.test.tsx

dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~UploadApplicationsCommandTests|FullyQualifiedName~OptionalUploadSessionTests|FullyQualifiedName~UploadSettingsTests|FullyQualifiedName~OptionalUploadEndpointsTests|FullyQualifiedName~UploadSettingsEndpointsTests|FullyQualifiedName~UploadSettingsPageTests"
```

These are release-time compatibility gates, not substitutes for the FR-010H cross-path fingerprint
matrix or the new bulk/raster/actor/notification suites.

Broad production release requires all of:

- STACK-B-LIVE-GATE and B-GATE, each including complete expanded audit-matrix evidence;
- STACK-A-LIVE-GATE and A-GATE, each including complete expanded audit-matrix evidence;
- PARITY-GATE;
- repaired FR-002N/SC-029 proof that Production fails closed without the explicit assertion,
  controlled Development/QA remains subject to normal product authorization, and two source tenants
  produce the same gate result without tenant inspection;
- verified publisher status and publisher-domain verification;
- the sanitized commercial-cloud/registration command evidence from section 2;
- FR-010B/SC-030 exact page count/order, ±1-point dimension, every-page SSIM >= 0.95, and
  blank/crop rejection evidence from both stack entry points;
- FR-010G type/magic/structural-document/page/configured-size validation before scoring in both
  stacks, with invalid converted output proven absent from application/scoring handoff;
- FR-010C retained-original/default-view/original-download/page-count evidence;
- FR-010H/SC-015 cross-path, cross-stack, and pre-change fingerprint evidence;
- FR-019/SC-031 authorized-detail actor/time and display-name-to-actor-ID fallback evidence;
- FR-020/SC-032 submitter-only recipient, accurate terminal counts, authorized link, zero item
  names, and exactly one retained idempotent acknowledgment evidence;
- FR-021 SQLite/Azure SQL job-lifetime retention, deletion cleanup, and audit-survival evidence;
- every expanded Principle I control-plane/request/item/content audit-matrix row with atomic
  state/event or durable outbox/reconciliation evidence, correlation ID, user or explicit service
  actor, UTC timestamp, immutable retention, prohibited-payload scan, and identical safe facts
  across both stacks and both providers;
- SC-002 evidence from each stack showing confirmation acknowledgment and the first successful
  authorized query of the returned request at or below 60 seconds on one stopwatch;
- named Stack A and Stack B legacy upload/settings suite results;
- no app-only permission, fallback identity, or tenant/folder allow-list in code, configuration,
  infrastructure, or deployed connector permissions.
