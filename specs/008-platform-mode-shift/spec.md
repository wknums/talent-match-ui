# Feature Spec: 008 Platform-Mode Orchestration Shift

**Feature ID:** 008-platform-mode-shift
**Status:** Draft
**Owners:** Client repo (Stack A + Stack B)

## Summary

When `AWR_PLATFORM_API_ENDPOINT` differs from `AWR_SEQ_API_ENDPOINT`, production scoring runs in platform mode. The client must submit batches, poll status, reconcile results, and keep SQL as the structured system of record. Document bytes must remain readable across both modes without requiring duplicate byte storage in SQL and Azure Blob for the same document row.

## Problem Statement

The client currently supports both sequential and platform orchestration modes, but records created in one mode must remain usable when the other mode is active. The storage contract must align with the constitution and code:

1. `STORAGE_PROVIDER` keeps constitution meaning (`local` | `azuresql`).
2. Blob usage is controlled by blob-specific settings, not by a third storage provider value.
3. Read paths must resolve bytes from Blob URI when present, with DB fallback.

## Goals

1. Keep mode detection stable (`AWR_PLATFORM_API_ENDPOINT` vs `AWR_SEQ_API_ENDPOINT`).
2. Keep SQL as system of record for structured entities and document metadata.
3. Support per-row canonical byte source:
   1. Blob URI object when `BlobUri` exists.
   2. DB blob content when `BlobUri` is absent.
4. Avoid duplicate byte storage across Azure Blob and DB blob storage for the same document row.
5. Maintain Stack A and Stack B parity.

## Non-Goals

1. Replacing sequential mode.
2. Requiring Azurite for local development.
3. Introducing `STORAGE_PROVIDER=azureblob`.

## Functional Requirements

- **FR-008-001 Mode Detection:** The system MUST resolve scoring mode by comparing `AWR_PLATFORM_API_ENDPOINT` and `AWR_SEQ_API_ENDPOINT`.
- **FR-008-002 Platform Submission + Idempotency:** In platform mode, client MUST submit JSON batch payloads to `POST /assess/batch` with `Idempotency-Key` where key value equals `batchId`.
- **FR-008-002A Idempotency Retention Window:** Platform idempotency dedupe MUST be retained until the originating batch reaches a terminal state, and then for an additional 30 days.
- **FR-008-003 Polling:** Client MUST poll `GET /assess/batch/{submissionId}/status` until terminal state.
- **FR-008-004 Cancellation:** Client MUST support `POST /assess/batch/{submissionId}/cancel`, stop scheduling new work for that submission, and reconcile terminal state including in-flight CV outcomes.
- **FR-008-005 Storage Provider Semantics:** `STORAGE_PROVIDER` MUST remain `local` or `azuresql` only.
- **FR-008-006 Platform Preconditions:** Platform mode in this repo MUST require `STORAGE_PROVIDER=azuresql` and `AWR_BLOB_STORAGE_ACCOUNT`.
- **FR-008-007 Per-Row Canonical Bytes:** Client MUST determine canonical bytes per document row:
  - `BlobUri` present => Azure Blob object is canonical.
  - `BlobUri` absent => DB blob content is canonical.
- **FR-008-008 Cross-Mode Read Compatibility:** Sequential and platform code paths MUST read document bytes correctly for both canonical cases.
- **FR-008-009 No Duplicate Byte Storage:** For new writes, bytes MUST be stored in exactly one location per document row across Azure Blob and DB blob columns.
- **FR-008-010 Metadata Persistence:** When uploading to Azure Blob in platform mode, client MUST persist `BlobUri` and `ContentSha256` in `ApplicationDocuments`.
- **FR-008-011 Legacy Fallback:** Existing DB-inline records MUST remain readable without migration.
- **FR-008-012 Security:** SAS tokens are forbidden; managed identity + RBAC only.
- **FR-008-013 Stack Parity:** Stack A and Stack B MUST implement equivalent storage semantics and mode behavior.
- **FR-008-014 Mixed Terminal Batch Semantics:** A batch MUST be marked `completed` when all CV entries in that batch are in terminal per-CV states (`completed`, `failed`, or `cancelled`) and each per-CV terminal outcome is persisted.
- **FR-008-015 Failed-CV Retry Split:** The system MUST support creating a new retry batch containing only failed CV entries from a prior completed batch; the retry batch MUST use a new `batchId` and new `Idempotency-Key`, and the original batch record MUST remain immutable.
- **FR-008-016 Recruiter/Admin Application Search:** The recruiter and admin UI in both Stack A and Stack B MUST provide search by applicant name for job applications, matching MUST be case-insensitive, and selecting a found application MUST open the existing application detail page.

## Non-Functional Requirements

- **NFR-008-001 Reliability:** Reconciler operations must be crash-safe through SQL leases and retries.
- **NFR-008-002 Observability (Structured Logs):** Submission, polling, cancellation, and fallback paths MUST emit structured logs including `correlationId`, `jobId`, `batchId`, `submissionId` (when available), terminal status, and operation duration.
- **NFR-008-004 Observability (Metrics + Alerts):** The system MUST emit counters and latency metrics for submit/poll/cancel/fallback operations and define alert thresholds for sustained failures.
- **NFR-008-005 Decision Trace Retention:** Per-CV decision traces (final decision inputs and outputs) MUST be retained for the configured audit retention period.
- **NFR-008-003 Backward Compatibility:** Existing rows remain usable without destructive data migration.

## Measurable Criteria

- **MC-008-001 Terminal Semantics:** In a mixed-outcome batch test, 100% of CV entries reach terminal per-CV states and the parent batch transitions to `completed` only after all entries are terminal.
- **MC-008-002 Retry Split Correctness:** In a retry scenario, the new retry batch contains only failed CV IDs from the source batch, uses a distinct `batchId`, and does not mutate terminal outcomes on the source batch.
- **MC-008-003 Idempotency Window:** Re-submitting an identical request with the same `Idempotency-Key` during active processing or within 30 days after terminal state MUST return the original submission identity and MUST NOT create a duplicate batch.
- **MC-008-004 Structured Log Coverage:** 100% of submit/poll/cancel/fallback flows in integration validation emit logs containing required identifiers and terminal status.
- **MC-008-005 Decision Trace Auditability:** Per-CV decision traces are queryable for the full configured audit retention period.
- **MC-008-006 Applicant Search Findability:** Recruiter and admin users in both Stack A and Stack B can locate a specific job application by applicant name regardless of letter case and open the existing application detail page from search results.

## User Stories

### US-008-01 Recruiter runs job in platform mode

As a recruiter, when platform mode is enabled, I can process a job and the client submits batches asynchronously and updates progress.

#### Acceptance Criteria (US-008-01)

1. Batches are enqueued in SQL and submitted by reconciler.
2. `submissionId` and `pollUrl` are persisted before submitted state is finalized.
3. Job progresses to completed/failed/cancelled with per-batch accounting.
4. For mixed CV outcomes, the batch reaches `completed` only when all CV entries are terminal and per-CV terminal outcomes are persisted.
5. Failed CV entries can be retried by creating a new batch that includes only failed CV IDs.

### US-008-02 Operator switches modes

As an operator, I can run sequential mode after platform-mode runs (and vice versa) and existing document records remain usable.

#### Acceptance Criteria (US-008-02)

1. If `BlobUri` exists, sequential read path can fetch bytes from Azure Blob.
2. If only DB blob content exists, both modes can still process documents.

### US-008-03 Security and governance

As a platform engineer, I can enforce RBAC-only blob access while keeping constitution-aligned storage configuration.

#### Acceptance Criteria (US-008-03)

1. No `STORAGE_PROVIDER=azureblob` requirement.
2. Blob reads/writes use managed identity credentials.
3. SAS token usage is not introduced.
4. Submit/poll/cancel/fallback logs include correlation and batch identifiers for diagnosis.
5. Per-CV decision trace records are retained for the configured audit retention period.

### US-008-04 Recruiter/Admin searches by applicant name

As a recruiter or admin, I can search job applications by applicant name without case sensitivity and open the matching application in the existing application detail page.

#### Acceptance Criteria (US-008-04)

1. Recruiter and admin UI surfaces in both Stack A and Stack B expose search by applicant name.
2. Search matching is case-insensitive for applicant name input.
3. Selecting a search result opens the current application detail page for that application.

## Edge Cases

1. Blob URI exists but blob read fails: client should attempt DB fallback when available.
2. Legacy record has DB bytes only: platform mode can upload and persist blob metadata.
3. Local development with `STORAGE_PROVIDER=local`: sequential mode remains fully functional.
4. Platform endpoint configured without blob account: fail fast with explicit error.
5. Applicant-name search returns multiple matches (including case-variant duplicates): recruiter/admin can review matching applications and open the intended record in the existing application detail page.

## Open Items

1. Stack B should consume per-CV aggregated payload consistently with Stack A behavior.
2. Historical `specs/001` platform contract references should be synchronized to avoid drift.
