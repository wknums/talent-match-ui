# Stack B optional-upload contract map

## Compatibility boundary

The existing `POST /api/jobs/{jobId}/applications/upload` multipart endpoint remains
outside the optional-upload OpenAPI contract. Its Web endpoint, MediatR command,
publication transaction, `Queued` result, scoring-queue pulse, client progress, dialog
close rules, validation, and duplicate behavior are not refactored. With the opt-in
control disabled, no new settings/session/item API or repository is contacted.

No Stack A source, test, schema, or migration may change until T043 records a passing
B-GATE.

## Layer ownership

| Contract operation | Web ownership | Application ownership | Domain/Infrastructure ownership |
|---|---|---|---|
| `getUploadSettings` | `UploadSettingsEndpoints` authorization/HTTP mapping | `GetUploadSettingsQuery` | `IUploadSettingsRepository` / EF repository |
| `updateUploadSettings` | `UploadSettingsEndpoints` global-admin adapter | `UpdateUploadSettingsCommand` validation/audit | `UploadSettings` / optimistic EF save |
| `createUploadSession` | `UploadEndpoints` job authorization/HTTP mapping | `CreateUploadSessionCommand` | `UploadSession`, `UploadItem` / atomic EF transaction |
| `listOwnedUploadSessions` | `UploadEndpoints` authenticated adapter | Upload session query service | owner/job-scoped repository read |
| `getUploadSession` | `UploadEndpoints` authenticated adapter | reconciliation/query service | aggregate and stale-item repository transaction |
| `heartbeatUploadSession` | `UploadEndpoints` authenticated adapter | lifecycle service | optimistic session update |
| `updateClientUploadItemStatus` | `UploadEndpoints` authenticated adapter | lifecycle transition service | legal transition and atomic aggregate update |
| `uploadItemContent` | `UploadEndpoints` one-file multipart adapter | `UploadItemContentCommand` validation/idempotent publication | occurrence/fingerprint claim and terminal replay |

## Entity ownership

| Entity | Domain | Application | Infrastructure | Web |
|---|---|---|---|---|
| `UploadSettings` | defaults and invariants | validation, query/update, audit orchestration | EF mapping and optimistic repository | admin-only DTO/endpoint/page |
| `UploadSession` | immutable limit snapshot and completion invariant | creation, aggregates, heartbeat reconciliation | atomic session/items transaction and scoped reads | owner/job-authorized DTO/endpoints/status surface |
| `UploadItem` | eight-state machine, attempt ceiling, write-once application | eligibility, retry classification, idempotent completion | uniqueness, fingerprint claims, atomic outcomes | one-file adapter and typed client/coordinator |

Provider-specific locking and SQL remain in Infrastructure. Web projects resolve
abstractions and do not import EF Core implementations.
