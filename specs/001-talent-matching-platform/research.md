# Research — Talent Matching Platform

**Feature**: 001-talent-matching-platform | **Date**: 2026-03-09 (updated 2026-04-23)

This document resolves all unknowns identified during Technical Context analysis.

---

## 2026-04-23 Addendum: Manual Review AI Evidence Prepopulation

### Q1: Why is evidence missing from category notes in Stack B?

**Decision**: Regression is in `ParseSingleRun` (`ScoreApplicationCommand.cs`), not in
`PrePopulateFromAiAsync`. Two defects cause evidence citations to have empty or wrong snippets:

1. **Case-sensitive `TryGetProperty("evidence")`** in the nested-object branch — if the LLM
   returns `"Evidence"` (capitalised), zero citations are stored for that category.
2. **Position-dependent `ExtractCategoryName` / `ExtractStringField`** in the array-of-objects
   branch — if JSON field order puts the evidence string before the category name, the fields
   are swapped, so citations are stored under the wrong key and `MatchCategory` can't find them.

Stack A is unaffected because it uses already-stored and deserialized `evidenceCitations` from
the API response; it does not re-derive evidence from raw LLM output at display time.

**Alternatives considered**: Changing the prompt to force a fixed JSON structure — rejected;
schema-agnostic parsing is intentional. Fix the parser's field-resolution logic instead.

### Q2: Is Stack A working correctly per FR-014?

**Decision**: Functionally correct for the score+evidence case. One spec gap: `aiScoringMismatch`
is a boolean (overall) but FR-014 requires a per-category mismatch warning. Extend
`StackBPrepopulationResult` to include `mismatchedCategories: string[]`.

### Q3: Canonical `EvidenceCitation` schema

```typescript
interface EvidenceCitation {
  category:    string    // rubric category name as returned by the LLM
  snippet:     string    // direct quote from the CV
  section?:    string    // optional: document section heading
  confidence?: number    // optional: 0–1
}
```

`EvidenceCitationsJson` stores lowercase-keyed JSON: `[{"category":"...","snippet":"..."}]`.
`PropertyNameCaseInsensitive = true` on the Stack B deserializer handles this correctly.

### Q4: Is `MatchCategory` / `matchCategoryToRubric` equivalent across stacks?

**Decision**: Yes — both implement exact → substring containment → ≥40% word overlap. No change
needed.

### Q5: Existing test coverage for evidence prepopulation

**Decision**: None. `Domain.Tests/JobTests.cs` only asserts `EvidenceCitationsJson = "[]"` for
the error path. New unit tests required before fixing (test-then-fix).

### Decision Summary

| # | Decision |
|---|----------|
| 1 | Fix `TryGetProperty("evidence")` → case-insensitive enumeration in `ParseSingleRun` |
| 2 | Fix field-resolution in array-of-objects branch (`ExtractCategoryName` / `ExtractStringField`) |
| 3 | Add `mismatchedCategories: string[]` to `StackBPrepopulationResult`; render per-category warnings |
| 4 | Write unit tests before fixing (regression capture first) |
| 5 | No schema migration needed; no prompt change needed |
| 6 | `humanEdited` persisted boolean is the sole prepopulation skip gate (clarification 2026-04-24) |

### 2026-04-24 Implementation Notes (T001–T033)

**Files changed**:
- `dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs` — `TryGetPropertyCaseInsensitive` helper (T007/T011); `ExtractCategoryName` prefers `category`/`name`/`label` properties (T012); `ExtractStringField` prefers `evidence`/`justification`/`snippet` properties (T012); empty-snippet guard added before appending to citation list (T013)
- `dotnet/src/Web.Client/Pages/ManualReview.razor` — `mismatchedCategories`/`hasPerCategoryMismatch` state (T006/T015); evidence dedup with trim+case-insensitive comparison (T014); per-category amber warning rendering (T016); banner behavior hardened for PR-1/PR-2/PR-3/PR-4 (T017); `HumanEdited` gate replaces `hasMeaningfulContent` (T030/T031)
- `src/lib/stackb-scoring.ts` — `mismatchedCategories` in `StackBPrepopulationResult` interface and `buildStackBManualReviewPrepopulation` return values (T004/T018); `hasMeaningfulManualReviewContent` now gates on `humanEdited === true` (T030)
- `src/components/ManualReviewView.tsx` — `mismatchedCategories` state + per-category warnings (T005/T019); `humanEdited` delta detection on save (T031)
- `server/storage/schema.sql`, `schema-sqlite.sql`, `db.ts`, `repos/application-repo.ts`, `routes/applications.ts` — `HumanEdited` column and full read/write pipeline (T027/T029)
- `dotnet/src/Domain/Entities/ManualReviewData.cs`, `SaveManualReviewCommand.cs`, `ApplicationsEndpoints.cs`, `ApiClient.cs`, `AppDbContext.cs`, `ApplicationRepository.cs`, `Program.cs` — `HumanEdited` field across all Stack B layers (T026/T028/T029)

**Test results** (all passing as of 2026-04-24):
- Stack B: 17 `ScoreApplicationCommandEvidenceParsingTests` + `ManualReviewPrepopulationTests` ✅
- Stack B: 3 `SaveManualReviewCommandTests` + 3 `ApplicationRepositoryTests` (includes `humanEdited` persistence) ✅
- Stack A: 16 `stackb-scoring.test.ts` tests ✅

---

## R1: ScoringPrompt Entity — Not Yet in Codebase

**Context**: The spec (US3a, FR-032–FR-041) defines a `ScoringPrompt` entity with versioning, activation, rating, and production approval. Neither `src/types/index.ts` (Stack A) nor `dotnet/src/Domain/Entities/` (Stack B) currently contains this entity.

**Decision**: Add `ScoringPrompt` as a new first-class entity in both stacks.

**Rationale**: US3a is a hard prerequisite for production scoring. The entity must support immutable versioning (editing creates a new revision), single-active-per-job semantics, 0–5 rating, free-text comments, and a status lifecycle (`draft` → `active` → `inactive` → `production-approved`). This directly serves FR-035 (immutable versioning) and FR-036 (single active prompt per job).

**Alternatives Considered**:
- Embed prompt text in `JobConfigVersion` — rejected because prompts have an independent lifecycle (multiple revisions per config version, separate approval workflow) and per-revision metadata (rating, comments).
- Store as untyped KV blobs — rejected because the audit trail and status transitions require structured fields.

**Implementation Approach**:
- Stack A: Add `ScoringPrompt` interface to `src/types/index.ts`. Add KV storage keys in `server/storage/kv-keys.ts`. Add prompt routes in `server/routes/prompts.ts`.
- Stack B: Add `ScoringPrompt` entity in `Domain/Entities/`. Add `IScoringPromptRepository` in `Domain/Interfaces/`. Add EF Core mapping and migration in Infrastructure. Add CQRS commands/queries in Application. Add API endpoints in Web.Server.

---

## R2: PromptTestRun Entity — Not Yet in Codebase

**Context**: The spec defines a `PromptTestRun` entity linking test-case applications to a prompt revision for the test-and-approve workflow (US3a, FR-038–FR-040).

**Decision**: Add `PromptTestRun` as a new entity in both stacks.

**Rationale**: Test scoring runs must be distinguished from production runs to prevent contamination of ranked lists (FR-038). The entity tracks which applications were scored as test cases, links them to a specific prompt revision, and tracks approval status.

**Alternatives Considered**:
- Add a `isTestCase` boolean to `Application` — rejected because a single application could theoretically be used in multiple test runs across different prompt revisions, and we need to track per-test-run approval status.
- Use a tag/label system — rejected as over-engineered for a simple test-case tracking need (violates YAGNI) and lacks the structured status tracking required.

**Implementation Approach**:
- Stack A: Add `PromptTestRun` interface to `src/types/index.ts`. Store via KV with prompt-test-run keys.
- Stack B: Add `PromptTestRun` entity to Domain. Add repository interface. Add EF Core migration.

---

## R3: External API Contract — AWR_SEQ_API_ENDPOINT

**Context**: The spec references `AWR_SEQ_API_ENDPOINT` for document extraction (US3 — extract job spec/rubric) and prompt generation (US3a — generate draft prompt from rubric). The existing codebase already has `POST /api/jobs/extract-spec` and `POST /api/jobs/extract-rubric` routes that call `AWR_SEQ_API_ENDPOINT/assess/passthrough`.

**Decision**: Prompt generation (US3a) will use the same `AWR_SEQ_API_ENDPOINT/assess/passthrough` endpoint pattern, with a prompt-generation-specific payload.

**Rationale**: The existing extraction routes demonstrate the integration pattern — the server constructs a request with a system prompt and user content, sends it to the external API, and returns structured JSON. Prompt generation follows the same pattern: send the approved rubric as context with a system prompt requesting structured scoring prompt output.

**Alternatives Considered**:
- Add a separate `/assess/generate-prompt` endpoint on the external API — rejected because the passthrough endpoint is designed for flexible AI tasks and avoids coupling to a specific API version.
- Call OpenAI/Azure OpenAI directly via `server/routes/llm.ts` — this is a valid fallback but the spec specifically mentions `AWR_SEQ_API_ENDPOINT` for rubric-related AI operations.

**Implementation Approach**:
- Reuse the existing passthrough pattern from `extract-spec`/`extract-rubric` routes.
- Add `POST /api/jobs/:jobId/prompts/generate` route that sends the job's approved rubric to `AWR_SEQ_API_ENDPOINT/assess/passthrough` with a prompt-generation system instruction.
- Stack B: Add equivalent endpoint in Web.Server, calling through `LlmProxyService`.

---

## R4: Prompt Versioning — Immutable Revision Strategy

**Context**: FR-035 requires that editing a prompt creates a new revision without altering the original. FR-036 requires only one active prompt per job at any time.

**Decision**: Use sequential integer version numbers per job, with a `status` field controlling the lifecycle.

**Rationale**: Integer versions are simple, human-readable, and sortable. The status enum (`draft`, `active`, `inactive`, `production-approved`) provides clear lifecycle management. Deactivation of the previous prompt when a new one is activated is handled as a transactional operation.

**Alternatives Considered**:
- UUID-based versioning — rejected because version ordering matters for the UI dropdown (FR-036 scenario 6) and integers are more intuitive.
- Git-like branching/merging — rejected as massive over-engineering for sequential prompt revisions (YAGNI).

**Implementation Approach**:
- Version numbers auto-increment per job (1, 2, 3, ...).
- Activation sets the selected version to `active` and all others for that job to `inactive` in a single transaction.
- Production approval transitions an `active` prompt to `production-approved`.

---

## R5: Test-and-Approve Workflow — Scoring Pipeline Integration

**Context**: US3a requires uploading test applications, scoring them with the active prompt, marking results as test cases, reviewing via manual review (US7), and approving the prompt for production only if no score changes were needed.

**Decision**: Reuse the existing upload (US4) and scoring (US5) pipeline, adding a `testRunId` field to applications and scoring runs created during testing. Filter test-case results out of production ranked lists.

**Rationale**: The scoring pipeline already handles document extraction, multi-run scoring, and aggregation. Adding a test-run context avoids duplicating this logic. The `PromptTestRun` entity provides the grouping and approval tracking.

**Alternatives Considered**:
- Separate test-only scoring pipeline — rejected because it would duplicate extraction, scoring, and aggregation logic (violates DRY and YAGNI).
- Temporary/ephemeral results — rejected because test results must persist for review and audit trail requirements.

**Implementation Approach**:
- Add optional `testRunId` to `Application` and `ScoringRun` entities.
- When `testRunId` is present, exclude from production ranked list queries.
- `PromptTestRun` tracks the set of test applications, the prompt revision used, and approval status.
- Manual review of test cases uses the same US7 interface — completion without changes enables the "Approve for Production" action.

---

## R6: AWReason Engine API Authentication

**Context**: The AWReason HTTP engine API (`AWR_SEQ_API_ENDPOINT`) has introduced authentication controlled by an `AUTH_MODE` environment variable on the engine side. All callers (extraction, prompt generation, scoring) must now send appropriate credentials depending on the configured mode. Previously, the API accepted all requests without authentication.

**Decision**: Introduce a shared authentication helper that decorates all outbound requests to `AWR_SEQ_API_ENDPOINT` with the correct headers based on a new `AWR_AUTH_MODE` environment variable. Use `AWR_`-prefixed env vars to avoid collision with the platform's own `AUTH_MODE`.

**Rationale**: The platform already has its own `AUTH_MODE` / `API_KEY` for frontend→backend authentication. The AWReason API's auth is independent — it protects the LLM engine. Using `AWR_AUTH_MODE`, `AWR_API_KEY`, `AWR_AAD_ISSUER`, and `AWR_AAD_AUDIENCE` makes the separation explicit and avoids ambiguity.

**Auth Modes**:

| Mode | `AWR_AUTH_MODE` | Headers sent | Use case |
|------|----------------|--------------|----------|
| No auth | `none` | None | Local dev |
| API key | `apikey` | `X-Api-Key: <AWR_API_KEY>`, `X-User-Id: <username>`, `X-User-Role: <role>` | Staging |
| Entra ID | `entra` | `Authorization: Bearer <JWT>` (client-credentials via MSAL / DefaultAzureCredential) | Production |

**Alternatives Considered**:
- Reuse the existing `AUTH_MODE` / `API_KEY` env vars — rejected because those control the platform's own inbound auth (frontend→backend). The AWReason API auth is a separate concern with different credentials and potentially different modes in the same deployment.
- Hardcode API key in source — rejected for obvious security reasons; secrets must come from environment variables or key vault.
- Per-call auth configuration — rejected as over-engineered. All calls to the same endpoint use the same auth mode.

**Implementation Approach**:
- Stack A: Create a shared helper function (e.g. `getAwrAuthHeaders(user)` in `server/services/awr-auth.ts`) that reads `AWR_AUTH_MODE` and returns the appropriate headers. Call it from `server/routes/jobs.ts` (extraction), `server/routes/prompts.ts` (prompt generation), and `server/workers/scoring.ts` (scoring). For `entra` mode, use `@azure/identity` `DefaultAzureCredential` with `.getToken()`.
- Stack B: Create a shared helper or `DelegatingHandler` (e.g. `AwrAuthHandler`) in `Infrastructure/Services/` that decorates `HttpClient` requests to the AWReason endpoint. For `entra` mode, use `Azure.Identity.DefaultAzureCredential`. Register as a transient handler in DI.
- Validate required env vars at startup: `AWR_API_KEY` required when `AWR_AUTH_MODE=apikey`; `AWR_AAD_AUDIENCE` required when `AWR_AUTH_MODE=entra`.
- Health endpoints (`/healthz`, `/ready`) are unauthenticated — no headers needed for readiness checks.

---

## R6: Document Upload Extraction — Supported Formats and Flow

**Context**: US3 specifies supported document types: PDF, JPG, MD, TXT, DOCX. The extraction calls `AWR_SEQ_API_ENDPOINT/assess/passthrough` for AI-based content extraction.

**Decision**: Use the existing extraction route pattern (`POST /api/jobs/extract-spec`) which already handles file content + MIME type and forwards to the external API.

**Rationale**: The route already exists and handles the passthrough pattern. The external API performs the actual extraction (OCR for images, text extraction for PDFs, etc.). The client sends base64-encoded file content with MIME type.

**Alternatives Considered**:
- Client-side extraction — rejected because it would expose AI API keys and violate Principle V (LLM Integration Discipline).
- Separate extraction microservice — rejected as over-engineering for the current single-tenant deployment model (YAGNI).

---

## R7: Real-Time Updates — SignalR vs Polling

**Context**: The system communication diagram shows Azure SignalR for live updates. Stack A uses 30-second polling. Stack B references SignalR Client NuGet package.

**Decision**: Stack A continues with polling (≤30s interval) for dashboard refresh. Stack B supports optional SignalR for real-time pipeline progress when the Azure SignalR service is available, with polling as fallback.

**Rationale**: The spec requires ≤30s refresh (SC-003), which polling satisfies. SignalR is an enhancement for Stack B's cloud deployment. The architecture supports both — polling is the baseline, SignalR is additive.

**Alternatives Considered**:
- Server-Sent Events for Stack A — viable but adds server complexity for marginal improvement over polling given the 30s requirement.
- WebSocket without SignalR — rejected for Stack B because Azure SignalR provides managed scaling.

---

## R8: Application Document Storage

**Context**: Stack A stores documents as base64 content in KV store entries. Stack B stores `ContentBase64` in the `ApplicationDocument` entity. The constitution mentions Blob Storage with RBAC (no SAS tokens).

**Decision**: For local development, continue storing document content inline (base64 in KV/SQLite). For production, documents should be uploaded to Azure Blob Storage with RBAC authentication, storing only the blob URL in the database.

**Rationale**: Inline storage is acceptable for development and small-scale testing. Production workloads with 20K+ documents per job require blob storage for performance and cost efficiency. The constitution explicitly mandates RBAC over SAS tokens for blob access.

**Alternatives Considered**:
- Always use blob storage — rejected for local development simplicity (YAGNI for dev environment).
- File system storage for Stack A — already implemented via the KV store's JSON file approach.

---

## R9: Password Hashing Consistency (Stack B)

**Context**: Stack B has a `PasswordHashConsistencyTests` test project, indicating past issues with hash computation differences between user creation and login flows. The constitution requires SHA-256 hashing (Principle IV).

**Decision**: Both stacks use `SHA-256(password)` as hex string for password hashing. Stack B must ensure `CreateUserCommand` and `AuthEndpoints` use the identical hashing implementation. FR-031 explicitly requires this.

**Rationale**: Hash inconsistency would prevent newly created users from logging in. The existing test project validates this. The implementation must use a shared hashing utility.

**Alternatives Considered**:
- bcrypt or PBKDF2 — superior security but the constitution specifically mandates `crypto.subtle` SHA-256 (Principle IV), and both stacks must produce compatible hashes.

---

*All NEEDS CLARIFICATION items resolved. Proceed to Phase 1: Design & Contracts.*

---

## R10: Scoring Worker Already Calls Passthrough (FR-045, FR-047)

**Context**: FR-045 requires the scoring worker to call `AWR_SEQ_API_ENDPOINT/assess/passthrough` with multipart FormData (`promptFile` + `specFile`). FR-047 requires the same calling convention as extraction/prompt-generation.

**Decision**: No scoring worker changes needed — both stacks already implement this correctly.

**Rationale**:
- **Stack A** `server/workers/scoring.ts`: Lines 75–81 build `FormData` with `promptFile` (resolved prompt as Blob) and `specFile` (candidate CV text as Blob), POST to `${AWR_SEQ_API_ENDPOINT}/assess/passthrough`. Lines 67–70 resolve `{{JOB_SPEC_TEXT}}` and `{{CANDIDATE_CV_TEXT}}` placeholders. Lines 90–120 parse the LLM JSON response into `ScoringRun` fields (eligibility_gate, rubric_scores, composite_score, improvement_recommendations).
- **Stack B** `LlmProxyService.ScoreAsync` (Infrastructure/Services/LlmProxyService.cs): Lines 55–72 build `MultipartFormDataContent` with `promptFile` and `specFile`, POST to `{endpoint}/assess/passthrough`. `ScoreApplicationCommand` (Application/Scoring/Commands/ScoreApplicationCommand.cs) resolves placeholders at lines 64–66 and parses JSON at lines 74–120.

**Alternatives Considered**: None — the implementation matches the requirement exactly.

---

## R11: Test Scoring Requires Production-Gate Bypass (FR-038, FR-046)

**Context**: The production scoring pipeline (`server/services/pipeline.ts` line 53; `JobsEndpoints.cs` line 225) enforces that a `production-approved` prompt must exist before scoring can run. FR-038 (amended) states the prompt under test does NOT need to be production-approved for test scoring. FR-046 requires real LLM scoring (not mocked) for test runs.

**Decision**: Add an optional `promptVersionId` parameter to the pipeline orchestrator. When provided (test-run context), skip the production-approved gate and use the given prompt directly for scoring. The scoring worker itself already accepts a `promptVersionId` parameter and uses it — the gate is only in the pipeline orchestrator.

**Rationale**: The scoring worker (`runScoring()` in Stack A) already accepts an optional `promptVersionId` and falls back to production-approved if not provided. The gate enforcement is in `processApplication()` which calls `getProductionApprovedPromptId()` and throws if null. By accepting an explicit `promptVersionId` override, test-run scoring bypasses only the gate lookup — scoring itself is identical (real LLM, same passthrough API, same response parsing).

**Alternatives Considered**:
- Add a separate `processTestApplication()` function — rejected because it would duplicate 90% of `processApplication()` logic (extraction, retry, DLQ, audit). Violates DRY.
- Temporarily mark the test prompt as `production-approved`, then revert — rejected because it creates a race condition in concurrent scenarios and violates audit trail integrity.
- Add a boolean `isTestRun` flag — rejected because passing the explicit `promptVersionId` is more precise and already supported by the scoring worker.

**Implementation Approach**:
- Stack A: Add optional `promptVersionId?: string` to `processApplication()`. When present, skip the `getProductionApprovedPromptId()` lookup and use the override directly.
- Stack B: `CreatePromptTestRunCommand` directly dispatches `ScoreApplicationCommand` with the test prompt ID — this already bypasses `JobsEndpoints.cs /process` entirely, so no endpoint change needed.

---

## R12: Auto-Trigger Scoring on Test Upload (FR-048)

**Context**: FR-048 requires that completing test-application upload automatically triggers the scoring pipeline. The current test-run creation endpoints (Stack A `POST /prompts/:promptId/test-runs`; Stack B `CreatePromptTestRunCommand`) create Queued applications but return immediately without triggering scoring. The user would need to manually hit a separate "Process" endpoint, which requires production-approved prompt (chicken-and-egg).

**Decision**: After inserting test applications, the test-run handler fires the scoring pipeline in the background (fire-and-forget) for each application, using the test prompt ID. The test-run status transitions: `pending_scoring` → `scoring` → `pending_review`.

**Rationale**: FR-048 explicitly states "no separate user action required to initiate test scoring." The auto-trigger must be non-blocking so the HTTP response returns promptly. The pipeline runs asynchronously, and status updates are visible via polling or the test-run GET endpoint.

**Alternatives Considered**:
- Synchronous scoring before returning the response — rejected because scoring N applications × M runs takes significant time (minutes). Would timeout HTTP connections.
- Queue-based dispatch (Service Bus, BullMQ) — rejected for local development simplicity (YAGNI). The fire-and-forget approach using `Promise` (Stack A) or `Task.Run` (Stack B) is sufficient for single-tenant deployment. Production scaling would use the message-based architecture from Principle VIII.
- Add a new `/process-test-run` endpoint — rejected because FR-048 says no separate action. The trigger is internal.

**Implementation Approach**:
- Stack A: After creating test applications, set test-run status to `pending_scoring`. Fire `Promise.allSettled()` of `processApplication()` calls (with `promptVersionId` override) in the background (not awaited in the request handler). When all complete, update status to `pending_review`.
- Stack B: After creating test applications, set status to `pending_scoring`. Use `Task.Run()` to dispatch scoring via `ISender.Send(ScoreApplicationCommand)` for each application. Update status to `pending_review` on completion.

---

## R13: PromptTestRun Status Enum Extension (FR-048)

**Context**: The current `PromptTestRun` status enum is `pending_review | approved | rejected`. FR-048 requires status transitions `pending_scoring → scoring → pending_review` during pipeline processing.

**Decision**: Extend the status enum to: `pending_scoring | scoring | pending_review | approved | rejected`.

**Rationale**: The new states make pipeline progress visible to the UI. `pending_scoring` is set immediately after test applications are created. `scoring` is set when the first application begins processing. `pending_review` is set when all applications have completed scoring and aggregation.

**Alternatives Considered**:
- Use the existing `pending_review` status for all states — rejected because the UI cannot distinguish between "waiting for pipeline" and "ready for manual review".
- Add a separate progress tracking mechanism — rejected as over-engineering when status values suffice (YAGNI).

**State Transition Diagram**:
```
pending_scoring → scoring → pending_review → approved
                                           → rejected
```
