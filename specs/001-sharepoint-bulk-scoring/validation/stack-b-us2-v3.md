# Stack B US2 validation (v3)

**Run date:** 2026-10-14  
**Current gate status:** PASS (T069 automated Stack B P2 gate)

## T058 intentional-red gate

The authorization-store tests are extended before T063 implementation. The exact intentional-red
command is:

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~RequestAuthorizationStoreTests"
```

The red result and subsequent focused green result are recorded below without using live Microsoft
Graph or identity-provider network calls.

### Intentional red — 2026-10-14

- Exit code: `1`
- Intended T063 gap: `RequestAuthorizationStore` only accepted
  `Func<IConfidentialClientApplication>` and therefore could not create the confidential client from
  the persisted tenant binding; the tenant-aware test failed to compile with `CS1593`.
- The solution also contained pre-existing compile failures in other dirty T059/T061/T062 test
  files. Those are outside T058/T063 and were not edited.

## T063 focused green

The T058 source was force-compiled while excluding only the unrelated dirty T059/T061 test cluster,
then the solution-level focused filter was run without rebuilding those unrelated files:

```text
dotnet test dotnet/TalentMatch.slnx --no-build --filter "FullyQualifiedName~RequestAuthorizationStoreTests"
```

Result: `Passed: 23, Failed: 0, Skipped: 0, Total: 23`.

Evidence covered by the focused suite:

- authorization-code MSAL cache silent use and refresh against the persisted tenant authority;
- complete request/actor/tenant/account/drive/folder binding and no alternate identity fallback;
- fresh data-key and wrapping-key rotation;
- lapse, revoke, access-loss, and post-lapse discard behavior;
- immutable establish/use/lapse/discard audits with correlation IDs, UTC timestamps, required user or
  explicit worker/timer actors, optional initiating user, and versioned prohibited-data-free facts;
- audit survival after authorization ciphertext deletion and SQLite/SQL Server model parity.

All identity-provider traffic was handled by the in-process fake HTTP handler; no live network was
used.

## T061 intentional-red worker gate

No T067 production implementation was made.

Build command:

```text
dotnet build dotnet\tests\Infrastructure.Tests\TalentMatch.Infrastructure.Tests.csproj --no-restore --verbosity:quiet
```

Result: `0 errors`.

Focused command:

```text
dotnet test dotnet\tests\Infrastructure.Tests\TalentMatch.Infrastructure.Tests.csproj --no-restore --no-build --filter "FullyQualifiedName~BulkScoringWorkerTests" --logger "console;verbosity=minimal"
```

Intentional-red result on 2026-10-14: `Failed: 8, Passed: 4, Skipped: 0, Total: 12`.

The retained failures prove the T067 gaps:

- restart-safe stage/create/handoff idempotency events are absent from the typed ledger;
- the item-claim query still references the obsolete unmapped `OwningStack` compatibility property,
  so the production claim path is not executable;
- claim, lease recovery/release, import, metadata success/failure, content stage/delete,
  conversion, application/document, handoff, item terminal, count reconciliation, and request
  terminal typed events are absent;
- no explicit ownership-checked `RenewLeaseAsync` operation exists;
- scoring handoff has no typed durable-outbox boundary;
- an injected SQLite typed-audit failure is bypassed and the item reaches `Scoring`, proving state,
  typed event, and inline-content decisions are not atomic.

The SQLite and SQL Server model-semantic cases both passed. The red tests additionally require
explicit non-`system` service actors, UTC timestamps, versioned safe facts, prohibited-payload
scans, durable outbox/reconciliation boundaries, downstream ownership of scoring start, and one
event per subject/type across restart. No network access or restore was used.

## T069 executable Stack B P2 gate

Exact command:

```text
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~RequestAuthorizationStoreTests|FullyQualifiedName~BulkAuthorizationLapseTests|FullyQualifiedName~BulkAuthorizationCleanupTests|FullyQualifiedName~BulkScoringWorkerTests"
```

Final result: **exit 0; 50 passed, 0 failed, 0 skipped**:

- Application authorization-cleanup tests: **7 passed**;
- Infrastructure authorization renewal, phase-aware lapse, and worker continuity tests:
  **43 passed**.

The first combined run exposed a real atomic-handoff persistence defect: the application document
row was committed without its separate inline content row. The repository correction now commits
both together and remains idempotent on retry. The rerun proves request-bound silent renewal,
immediate stop of new source reads, staged-content continuation, terminal/cancel-settled cleanup,
lease recovery and idempotent worker markers, and every applicable authorization/item/content
lifecycle row with correlation, user or explicit service actor, UTC timestamp, safe immutable
payload, durable atomic boundary, and SQLite/SQL Server semantics. No live identity provider,
Microsoft Graph, or SharePoint resource was accessed.
