# Research — Talent Matching Platform

**Feature**: 001-talent-matching-platform | **Date**: 2026-03-09

This document resolves all unknowns identified during Technical Context analysis.

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

## R3: External API Contract — AWRSEQAPI_ENDPOINT

**Context**: The spec references `AWRSEQAPI_ENDPOINT` for document extraction (US3 — extract job spec/rubric) and prompt generation (US3a — generate draft prompt from rubric). The existing codebase already has `POST /api/jobs/extract-spec` and `POST /api/jobs/extract-rubric` routes that call `AWRSEQAPI_ENDPOINT/assess/passthrough`.

**Decision**: Prompt generation (US3a) will use the same `AWRSEQAPI_ENDPOINT/assess/passthrough` endpoint pattern, with a prompt-generation-specific payload.

**Rationale**: The existing extraction routes demonstrate the integration pattern — the server constructs a request with a system prompt and user content, sends it to the external API, and returns structured JSON. Prompt generation follows the same pattern: send the approved rubric as context with a system prompt requesting structured scoring prompt output.

**Alternatives Considered**:
- Add a separate `/assess/generate-prompt` endpoint on the external API — rejected because the passthrough endpoint is designed for flexible AI tasks and avoids coupling to a specific API version.
- Call OpenAI/Azure OpenAI directly via `server/routes/llm.ts` — this is a valid fallback but the spec specifically mentions `AWRSEQAPI_ENDPOINT` for rubric-related AI operations.

**Implementation Approach**:
- Reuse the existing passthrough pattern from `extract-spec`/`extract-rubric` routes.
- Add `POST /api/jobs/:jobId/prompts/generate` route that sends the job's approved rubric to `AWRSEQAPI_ENDPOINT/assess/passthrough` with a prompt-generation system instruction.
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

## R6: Document Upload Extraction — Supported Formats and Flow

**Context**: US3 specifies supported document types: PDF, JPG, MD, TXT, DOCX. The extraction calls `AWRSEQAPI_ENDPOINT/assess/passthrough` for AI-based content extraction.

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
