# Quickstart: Optional Parallel File Uploads

This guide describes how an implementer will validate the planned feature in
`C:\code\awr-cv-match-client` after implementation. It does not replace the contract in
`C:\code\awr-cv-match-client\specs\001-parallel-file-upload\contracts\openapi.yaml`.

## 1. Prerequisites

- Node.js 20.19+ or 22.12+ and npm 10+
- .NET SDK 10.0.x
- A clean SQLite development database or an isolated copy of
  `C:\code\awr-cv-match-client\shared-data\talentmatch.db`
- One existing job with an approved production scoring prompt, as required by the
  current upload flow
- Accounts for:
  - global system administrator (`admin`)
  - recruiter with access to the job
  - organization administrator (Entra mode) for a negative Settings test
- Representative PDF, MD, DOCX, TXT, JPG, and PNG files around the 4 MiB boundary

Do not use real applicant data for test files.

## 2. Read the compatibility boundary first

Before implementing, confirm the old path still maps to:

- Stack A:
  `C:\code\awr-cv-match-client\src\components\UploadApplicationsDialog.tsx` →
  `C:\code\awr-cv-match-client\src\lib\api-real.ts` →
  `C:\code\awr-cv-match-client\server\routes\applications.ts`
- Stack B:
  `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Components\UploadApplications.razor` →
  `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Services\ApiClient.cs` →
  `C:\code\awr-cv-match-client\dotnet\src\Web.Server\Endpoints\ApplicationsEndpoints.cs` →
  `C:\code\awr-cv-match-client\dotnet\src\Application\Applications\Commands\UploadApplicationsCommand.cs`

The optional-off branch must still call these existing methods/endpoints with their
existing request, progress, close, duplicate, response, and scoring behavior. Do not
make the old endpoint create UploadSessions or consult UploadSettings.

## 3. Apply the additive schema in Stack B first

Stack B owns the first implementation of the shared contract and schema:

1. Add the C# entities and repository contracts for the three entities in
   `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\data-model.md`.
2. Add the EF Core mappings, migration, and model snapshot under
   `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence`.
3. Confirm an absent UploadSettings row reads as:
   - file concurrency: `4`
   - maximum individual file: `4,194,304` bytes
   - maximum in-flight raw bytes: `104,857,600` bytes
4. Confirm no existing Application, ApplicationDocument, DocumentBlob, upload request,
   or scoring row is changed or backfilled.
5. Validate apply and rollback/read compatibility for isolated SQLite and SQL Server test
   databases.

Do not change Stack A storage initialization yet. Run migration tests against an isolated
database or copy, never the shared developer database.

## 4. Implement Stack B in dependency order

1. Record the passing baseline for
   `UploadApplicationsCommandTests`,
   `UploadedApplicationPublicationTests`, and the upload cases in
   `JobUsabilityComponentsTests`.
2. Add Domain entities, status values, validation rules, and repository interfaces.
3. Add Infrastructure mappings, repositories, and the additive migration.
4. Add Application services for settings, sessions, items, legal transitions,
   idempotent completion, duplicate decisions, retry classification, heartbeat
   reconciliation, and existing `Queued` publication.
5. Add authorized Web.Server settings/session/item endpoints and observability.
6. Add typed Web.Client `ApiClient` methods.
7. Add the scoped Blazor WASM coordinator and deterministic count/byte scheduler.
8. Add Job Details Upload activity and pipeline drill-down, the dashboard Active Uploads
   count, and interruption recovery without mounting job details in the global layout.
9. Add the dialog opt-in control and optional-path branch without changing
   `UploadFiles` when the control is off.
10. Add **Settings** under **System Configuration** for global administrators.

Do not add a service worker, upload worker pool, source-file persistence, new scoring
controls, or a separate runner-count setting. Do not begin Stack A implementation during
these steps.

## 5. Start Stack B

From `C:\code\awr-cv-match-client\dotnet`:

```powershell
dotnet restore TalentMatch.slnx
dotnet run --project .\src\Web.Server\TalentMatch.Web.Server.csproj
```

Use the URL printed by ASP.NET Core. The Blazor scoped coordinator must survive route
navigation within this running WASM application.

## 6. Pass B-GATE before starting Stack A

First run the focused Stack B legacy-off suites and all new Stack B tests:

```powershell
dotnet test .\tests\Application.Tests\TalentMatch.Application.Tests.csproj --filter FullyQualifiedName~UploadApplicationsCommandTests
dotnet test .\tests\Infrastructure.Tests\TalentMatch.Infrastructure.Tests.csproj --filter FullyQualifiedName~UploadedApplicationPublicationTests
dotnet test .\tests\Web.Tests\TalentMatch.Web.Tests.csproj --filter FullyQualifiedName~JobUsabilityComponentsTests
dotnet test TalentMatch.slnx
dotnet build TalentMatch.slnx --configuration Release
```

B-GATE passes only when:

- the optional-off dialog still calls `ApiClient.UploadApplicationsAsync` once with the
  existing multipart batch and duplicate query value
- the existing endpoint still dispatches `UploadApplicationsCommand`
- existing all-or-none publication, `Queued` status, and scoring-queue pulse behavior
  retain their current expectations
- no legacy-off request creates an UploadSession/UploadItem or reads UploadSettings
- the new Stack B settings, authorization, durable-state, idempotency, duplicate,
  scheduler, retry, navigation, observability, provider-migration, and parser-alignment
  tests pass
- the full .NET suite and Release build pass

Record B-GATE as a blocking task/milestone. No Stack A source, test, or storage migration
task may start until it passes.

## 7. Implement and start Stack A after B-GATE

Only after B-GATE:

1. Add canonical TypeScript types matching the proven OpenAPI/status/schema contract.
2. Add matching idempotent SQLite and Azure SQL schema evolution without redesigning the
   Stack B-proven tables or constraints.
3. Add repository/service logic and thin authorized Express endpoints.
4. Add typed API methods, the React application-lifetime coordinator, and deterministic
   count/byte scheduler.
5. Add shell aggregate status, item details, the dialog opt-in branch, and global-admin
   Settings UI.
6. Update `C:\code\awr-cv-match-client\INTEGRATION.md` with equivalent endpoints.

From `C:\code\awr-cv-match-client`:

```powershell
npm install
npm run dev
```

Use the URLs printed by Vite/Express. Keep the originating browser tab open during the
active-transfer scenarios.

## 8. Pass A-GATE and the final parity gate

Run focused Stack A legacy-off and optional-path tests first, then:

```powershell
npm run test
npm run build
npm run lint
npm run test:e2e
```

A-GATE passes only when the existing base64 JSON batch route, duplicate behavior,
warnings, direct `Queued` persistence, progress, close behavior, and result interpretation
remain unchanged with the option off, while all Stack A optional-path tests pass.

The final parity gate then replays common OpenAPI/status fixtures, SQLite/Azure SQL schema
checks, and both stacks' focused legacy-off suites. Passing A-GATE does not replace or
waive B-GATE.

## 9. Validate Settings

As a global system administrator:

1. Open **System Configuration > Settings**.
2. With no saved row, verify `4`, `4 MiB`, and `100 MiB`.
3. Save valid alternate limits and reload; verify they persist.
4. Attempt each invalid value:
   - zero/negative/non-whole concurrency
   - zero/negative individual size
   - total in-flight bytes lower than individual bytes
5. Verify field-specific explanations and that the last valid values remain active.
6. Start a session, change Settings, and verify the active session retains its snapshot
   while a later session receives the new values.

As recruiter and organization administrator, verify Settings is absent and direct
GET/PUT calls return the existing canonical forbidden response. Confirm correlation IDs
are present and settings updates appear in the audit ledger.

## 10. Validate the unchanged default path

For each stack:

1. Open a new application-upload flow.
2. Verify **Allow parallel individual uploads** is off and the duplicate choice retains
   its existing default.
3. Select the same representative batch used by existing regression tests.
4. Upload without enabling the new option.
5. Verify:
   - the existing bulk endpoint is the only upload endpoint called
   - no UploadSession/UploadItem is created
   - current client and server validation/warnings are unchanged
   - current progress and dialog-close blocking are unchanged
   - current duplicate-disabled and duplicate-enabled results are unchanged
   - successful applications enter the same queued/scoring behavior

This is the release-blocking backward-compatibility check.

## 11. Validate optional browser-lifetime behavior

1. Begin a new upload flow and explicitly enable the optional control.
2. Select multiple valid files.
3. Start the upload and inspect network/database state:
   - one session and every selected occurrence exist before the first content request
   - each item has a distinct stable occurrence key
   - the session contains a settings snapshot
4. Close the dialog and navigate among in-app views.
5. Verify requests continue in the same tab; return to the originating Job Details page
   and verify its Upload activity surface reflects current durable state.
6. Inspect a session and verify filename, raw size, status, attempt count, and actionable
   terminal explanations.
7. Reload/close the tab during active work, wait beyond the heartbeat lease, and return:
   - completed server-known outcomes remain visible
   - unfinished nonterminal items are `interrupted`
   - no automatic source-file continuation is attempted
8. Repeat with 67 files and verify all 67 durable items reach an explicit terminal
   outcome. Short heartbeat/API delays below two minutes must not convert live work to
   `interrupted`.

## 12. Validate Stack B status placement and drill-down

1. Create at least two optional upload sessions for the same job with different item
   counts and creation times.
2. Open that job's Job Details page and verify Pipeline Status contains **Uploads** using
   the newest session's submitted count, not the largest or oldest session.
3. While work is active, verify:
   - waiting, throttled, and uploading items occupy the light-blue segment
   - failed and retrying items occupy the orange segment
   - the grey remainder increases as items become terminal
4. Activate **Uploads** and verify:
   - the URL remains `/jobs/{jobId}` and never becomes the dashboard route
   - the collapsed Upload activity section expands
   - the related newest session expands
   - focus and viewport move to Upload activity
5. Open the dashboard and verify:
   - Upload activity details are absent
   - **Active Uploads** appears between **Applications** and **Queued**
   - its value is the sum of `Total - Terminal` across visible owned sessions
6. Open each System Configuration page and verify Upload activity details are absent.
7. Expand System Configuration and verify each submenu hover/focus/active box covers the
   complete wrapped label.
8. Open Optional upload settings and verify its title size matches the other System
   Configuration pages, all three inputs share one aligned column, and the layout becomes
   one column on a narrow viewport.

## 13. Validate count and byte throttling

Configure a low, observable test profile, for example:

- concurrency: `2`
- maximum individual: `4 MiB`
- total in flight: `5 MiB`

Use files sized approximately 3 MiB, 3 MiB, 1 MiB, and 1 MiB.

Expected:

- no more than two content requests are active
- two 3 MiB files are never active together
- an eligible file lacking byte capacity is `throttled`, not failed
- capacity release starts waiting work in selection order
- aggregate waiting/active counts match item states
- scoring concurrency/configuration is unchanged

Repeat with 100 files and verify the configured count and raw-byte maxima are never
exceeded.

## 14. Validate size and type alignment

For the optional path in both stacks:

1. Upload one file exactly `4,194,304` bytes: eligible.
2. Upload one file `4,194,305` bytes alongside a valid file: the oversized item fails
   with a file-specific size explanation while the valid file proceeds.
3. Select only ineligible files: no session is created and the user receives a clear
   explanation.
4. Exercise PDF, MD, DOCX, TXT, JPG, and PNG with normal and empty browser MIME values.
5. Exercise an unsupported type: preserve the supported-type boundary and record a
   file-specific permanent outcome.
6. Verify persisted successful documents use the existing metadata/content format and
   are accepted by the current downstream parser/scoring submission.

Do not change the legacy path's current validator as part of these checks.

## 15. Validate duplicates and idempotency

Run all cases in both stacks:

| Duplicate choice | Case | Expected |
| --- | --- | --- |
| off | content already belongs to the job | item is `skipped_duplicate`, no application |
| off | same content appears twice in one selection | first eligible occurrence may succeed; later occurrence is `skipped_duplicate` |
| on | same content appears twice intentionally | each occurrence may create one distinct application |
| either | same content request repeated for one occurrence | canonical recorded result is returned; no extra application |
| either | response is lost after acceptance, then retried | no more than one application for that item |

Also issue two concurrent requests for the same occurrence and two concurrent
duplicate-disabled requests with matching content. Database constraints/transactions,
not client timing, must preserve the expected results.

## 16. Validate retries and isolation

Use a controllable test server/handler to produce:

- one transient timeout followed by success
- HTTP 408, 429, 500, and 503 followed by success
- four consecutive transient failures
- permanent 4xx validation and authorization responses

Expected:

- transient attempts progress through `retrying`
- there are at most four total attempts (initial + three additional)
- permanent failures are not retried
- exhausted transient work becomes `failed` with an actionable explanation
- one failed item does not stop, roll back, or alter unrelated items
- permits are released after every attempt, including thrown/lost responses

## 17. Validate observability

For one mixed-outcome session, correlate:

- session and item IDs
- job and actor
- settings snapshot
- each durable state transition
- attempt number and safe reason code
- raw bytes and duration
- terminal aggregate counts

Verify the same correlation ID connects HTTP response headers, structured logs, and
immutable ProcessingEvents. Confirm logs/events contain no file bytes, base64 payloads,
tokens, or secrets. Upload metrics must be separate from scoring worker/concurrency
metrics.

## 18. Run focused automated checks

Run Stack B checks first:

```powershell
dotnet test TalentMatch.slnx
dotnet build TalentMatch.slnx --configuration Release
```

Only after they pass, run Stack A checks:

```powershell
npm run test
npm run build
npm run lint
npm run test:e2e
```

During implementation, prefer focused test filters first, then run the complete suites.
The feature is not ready if existing upload tests require changed expectations while the
optional control is off.

## 19. Acceptance checklist

- [ ] B-GATE is recorded as passed before any Stack A implementation task starts.
- [ ] Every Stack A implementation task depends on B-GATE.
- [ ] A-GATE and the final cross-stack parity gate pass without waiving B-GATE.
- [ ] Optional control defaults off for every new selection flow.
- [ ] Legacy-off behavior and endpoint traffic are unchanged.
- [ ] Session and every occurrence are durable before transfer.
- [ ] Count and raw-byte limits are never exceeded.
- [ ] Capacity saturation waits and later proceeds.
- [ ] One file failure does not affect other files.
- [ ] Each occurrence creates at most one application across retries/replays.
- [ ] Duplicate-off/on outcomes match existing intent.
- [ ] Successful items use the existing queued/scoring path.
- [ ] Dialog close and in-app navigation do not stop active tab work.
- [ ] Reload/tab loss preserves terminal outcomes and marks stale work interrupted.
- [ ] Aggregate/item status meets the 2-second visibility target.
- [ ] Job Details Uploads drill-down preserves the route and expands/focuses the related session.
- [ ] Upload activity details appear only on Job Details.
- [ ] Dashboard Active Uploads is positioned between Applications and Queued and counts nonterminal items.
- [ ] System Configuration submenu states cover complete labels without clipping.
- [ ] Optional upload settings title and three field rows are consistently aligned and responsive.
- [ ] Only global administrators can read/save valid Settings.
- [ ] Existing sessions retain their settings snapshot.
- [ ] Parser/type/size boundaries align for the optional path.
- [ ] Audit/log/metric records are correlated and contain no source content.
