# 008 — Platform-Mode Orchestration Shift

Move scoring orchestration from the client web process to the platform
(Service Bus + Durable Functions) when `AWR_PLATFORM_API_ENDPOINT` is set.

## Documents

- **[platform-contract.md](platform-contract.md)** — Single source of truth for
  the wire contract between client, platform, and engine. Read this first.

## Status

| Phase | Status | Notes |
|---|---|---|
| Contract drafted | ✅ Done | See `platform-contract.md` |
| SQL schema (ScoringBatches, ScoringJobProgress, new columns) | ✅ Done | `server/storage/schema.sql`, `schema-sqlite.sql`, `server/storage/db.ts` |
| Blob storage abstraction (Node + .NET) | ⏳ Pending | New module `server/storage/blob.ts` and equivalent `IBlobStore` in .NET |
| Stack A: submit/reconcile split + SQL-lease reconciler | ⏳ Pending | Refactor of `server/services/pipeline.ts`; new `server/workers/reconciler.ts` |
| Stack A: cancel endpoint + flag plumbing | ⏳ Pending | New route under `server/routes/jobs.ts` |
| Stack B: platform-mode submitter + IHostedService reconciler | ⏳ Pending | New `IPlatformScoringService` + `PlatformScoringReconciler : BackgroundService` |
| Stack B: cancel command + check | ⏳ Pending | New `CancelJobScoringCommand` |
| Platform repo work | 🔒 Blocked on contract sign-off | See HANDOFF in contract doc §9 |
| Engine repo work | 🔒 Blocked on contract sign-off | See HANDOFF in contract doc §9 |

## Why this exists

At 100 k CVs the current per-application submit-then-poll loop is not viable:

- 100 k HTTPS handshakes + 300 k+ polls.
- All orchestration lives in the web process; PaaS recycle = lost work.
- No persisted `submissionId`, so restart = duplicate platform jobs.
- 15-minute poll wall = anything queued behind a backlog times out.

The shift makes the client a thin **submit / reconcile / report** layer over
durable platform-side orchestration.

## Key decisions (locked)

| Topic | Choice |
|---|---|
| Batch size | K=2 for MVP, configurable; contract supports K≥1 |
| Reconciliation | Polling only (webhook deferred) |
| Reconciler location | In-process timer + SQL row-lease (both stacks) |
| Schema | Shared `talentmatch` schema across stacks |
| Stack B scope | Full parity — implements submit + reconcile in .NET |
| Upload transport | Blob-by-reference, RBAC, **no SAS** (per constitution) |
| Cancellation | In MVP scope |

## Constraints from the constitution

- `.specify/memory/constitution.md` §SECURITY: **SAS tokens are forbidden** for
  blob access. Both client (writer) and platform (reader) use managed identity
  on the same `cv-uploads` container.
- SHA-256 content hashing per blob, stored in `ApplicationDocuments.ContentSha256`.

## Open questions (deferred to platform team)

See `platform-contract.md` §10 — six items requiring platform-side input before
implementation begins on the platform repo.
