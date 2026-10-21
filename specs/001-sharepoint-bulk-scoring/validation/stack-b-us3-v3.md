# Stack B US3 Validation — v3.1

**Run date:** 2026-10-14  
**Current gate status:** PASS (T083 automated Stack B P3 gate)

## T075 terminal notification and immutable audit contract — intentional RED

The no-network red-first command was:

```text
dotnet test dotnet\TalentMatch.slnx --no-restore --filter "FullyQualifiedName~BulkTerminalNotification"
```

**Observed RED (exit 1).** The focused Application suite compiled and reported **2 failed,
2 passed**:

- the notification is still the broad 18-field request summary rather than the dedicated
  submitter-only six-field notification with four frozen counts and a server-generated relative
  detail path; and
- terminal counts can still be changed after the first terminal transition.

The focused Web suite was also compiled and executed in isolation from unrelated dirty US3 test
files and reported **3 failed, 1 passed**:

- the endpoint still returns the broad request summary;
- finalization emits `bulk_request_terminal_finalized` but not the required
  `bulk_request_counts_reconciled` and `bulk_notification_created` rows; and
- the retained acknowledgment timestamp round-trips from SQLite as `Unspecified`, not UTC.

The provider-model/migration parity assertion passed. The new suites additionally exercise
submitter-only visibility, normal authorization on the server-generated relative detail path,
structural absence of item-name/list fields, first-finalizer idempotency, retained idempotent
owner acknowledgment, confirmation/cancellation/retry/reconciliation/finalization/notification
audit actor and correlation rules, UTC timestamps, immutable cleanup survival, prohibited-payload
scans, and matching SQLite/SQL Server schema contracts.

Running the Web project without the isolation exposed pre-existing compile errors in the dirty
T074/query test files (`StringComparison` passed to unsupported FluentAssertions overloads);
those files were preserved unchanged. No restore, login, tenant, SharePoint, SQL Server, or other
network operation was performed.

## T083 executable Stack B P3 gate

Exact command:

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkRequestActorDetail|FullyQualifiedName~BulkTerminalNotification|FullyQualifiedName~CancelBulkScoringRequest|FullyQualifiedName~RetryFailedBulkScoringRequest"
```

Final result: **exit 0; 44 passed, 0 failed, 0 skipped**:

- Application actor, notification, cancellation, and retry tests: **34 passed**;
- Web actor-detail and terminal-notification endpoint tests: **10 passed**.

The combined gate proves recorded-actor-ID-only display resolution with recorded-ID fallback and
no username fallback; phase-aware cancellation; fresh-bound retry with immutable original
requests; one submitter-only first-terminal notification with frozen counts and a narrow
item-name-free DTO; owner-only idempotent acknowledgment; and all applicable item-terminal,
retry-item, count-reconciled, terminal-finalized, and notification audit rows with atomicity,
correlation, authenticated user or explicit service actor, UTC timestamps, immutable survival,
safe payloads, and provider-parity assertions. No live Microsoft or deployment operation ran.
