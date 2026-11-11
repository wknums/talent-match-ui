# Stack B Delegated Foundation Gate — v3.1

**Run date:** 2026-10-13  
**Constitution:** 3.0.0  
**Scope:** T011–T028 only  
**Result:** PASS

This gate covers the local automated foundation for the dynamic per-user, multi-tenant delegated
Microsoft 365 connector. It is not live-tenant, publisher-verification, infrastructure-apply,
Stack B story, B-GATE, parity, or release evidence.

## Red-first record

Before implementation, the T011–T018 tests failed for the intended missing v3.1 behavior:

- pending connection and request authorization bindings/lifecycle;
- seven-table provider model and `DelegatedBulkSourceV3` migration;
- connection repository/atomic transfer and HMAC admission repository;
- associated-data envelope/key validation;
- delegated commercial-cloud options; and
- product-environment-only publisher activation.

No old app-only, `Sites.Selected`, source allow-list, same/cross-tenant, or OBO result was accepted
as current evidence.

## Validation

```text
dotnet build dotnet/TalentMatch.slnx --no-restore
PASS (exit 0)

dotnet test dotnet/TalentMatch.slnx --no-build --filter "FullyQualifiedName~Microsoft365PublisherGateTests"
PASS (exit 0): Application.Tests 10 passed

dotnet test dotnet/TalentMatch.slnx --no-build --filter "FullyQualifiedName~BulkSourceConnection|FullyQualifiedName~BulkRequestAuthorization|FullyQualifiedName~BulkSourceAdmission|FullyQualifiedName~AuthorizationEnvelope|FullyQualifiedName~Microsoft365SourceOptions"
PASS (exit 0):
- Domain.Tests: 8 passed
- Application.Tests: 2 passed
- Infrastructure.Tests: 8 passed
- Web.Tests: no matching tests
```

## Migration and security boundary

- EF migration `DelegatedBulkSourceV3` creates the admission and pending-connection tables, removes
  persisted access-mode/owning-stack selection, adds the delegated binding and frozen request
  fields, and retains completed history with safe defaults.
- The migration refuses active legacy draft/active work on SQLite and SQL Server. It does not
  translate old credential material.
- `BulkSourceMigrationPreflight` inventories active/draft work and credentials, requires drain, or
  performs only an explicitly operator-confirmed discard.
- Admission persistence stores only HMAC subjects and uses serializable transactions for
  cross-instance actor/IP/callback decisions.
- Connection-to-request authorization transfer copies protected material, consumes and
  cryptographically clears the pending record, and commits in one transaction.
- Production publisher activation is decided only from product environment plus the explicit
  publisher assertion, before source services/workers are registered.

## Gate limitations

US1 connection/callback/Graph/discovery/review/confirmation behavior begins at T029 and remains
separately gated. Real Entra registration, publisher, tenant consent, Graph, Azure SQL execution,
Terraform plan/apply, and live operational evidence remain outstanding.
