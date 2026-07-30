# Research - 008-platform-mode-shift

## Decision 1: Canonical byte source is per-row, determined by BlobUri

- Decision: Use per-row canonical bytes where `BlobUri` non-null means Azure Blob is canonical; otherwise DB content is canonical.
- Rationale: Supports cross-mode compatibility without forcing full migration and aligns with FR-008-007/008/011.
- Alternatives considered:
	- Global mode-based canonical source: rejected, breaks legacy row compatibility.
	- Mandatory one-time migration to blob: rejected, operationally risky and outside feature scope.

## Decision 2: Platform mode preconditions are explicit and strict

- Decision: Require `AWR_PLATFORM_API_ENDPOINT` != `AWR_SEQ_API_ENDPOINT`, `STORAGE_PROVIDER=azuresql`, and `AWR_BLOB_STORAGE_ACCOUNT` set.
- Rationale: Prevents half-configured platform-mode behavior and enforces shared SQL + blob-by-reference design.
- Alternatives considered:
	- Allow platform mode in `local` provider: rejected for shared-cloud workflow and consistency.
	- Auto-fallback silently to sequential mode when blob account missing: rejected because it masks misconfiguration.

## Decision 3: Keep STORAGE_PROVIDER semantics unchanged

- Decision: Keep `STORAGE_PROVIDER` values as `local|azuresql`; do not introduce `azureblob`.
- Rationale: Constitution principle III and feature FR-008-005 require provider semantics stability.
- Alternatives considered:
	- Add `azureblob` provider value: rejected due to governance conflict and unnecessary complexity.

## Decision 4: Polling reconciler remains the MVP completion mechanism

- Decision: Use SQL lease-based reconciler with periodic polling and exponential backoff.
- Rationale: Existing architecture already supports in-process workers/hosted services and provides crash-safe progress.
- Alternatives considered:
	- Webhook-only completion: rejected for MVP due to dependency complexity.
	- Direct synchronous submit-and-wait in request thread: rejected due to durability and scale constraints.

## Decision 5: Security model is managed identity + RBAC only

- Decision: Blob writes/reads use managed identity credentials and RBAC roles; no SAS issuance.
- Rationale: Constitution principle IV and FR-008-012.
- Alternatives considered:
	- SAS token handoff: rejected by policy.

## Decision 6: Result contract handling remains per-CV and backward compatible

- Decision: Treat per-CV `result.cvs[]` as the source of truth in platform mode and reuse existing run parsing + finalization logic.
- Rationale: Maintains parity with sequential scoring result semantics while supporting batch-level transport.
- Alternatives considered:
	- Introduce a new aggregated-only result format: rejected due to migration and parity risk.

## Decision 7: Open items resolution for this planning phase

- Decision: Track Stack B cancellation partial-completion handling and per-CV payload mapping as explicit implementation tasks.
- Rationale: These are known risks, but design direction is clear enough for planning artifacts.
- Alternatives considered:
	- Block planning until platform backend is complete: rejected; client-side planning and implementation can proceed with contract-first assumptions.

