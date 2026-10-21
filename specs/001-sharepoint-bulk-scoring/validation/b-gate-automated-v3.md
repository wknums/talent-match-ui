# Stack B Automated Gate

**Feature**: `001-sharepoint-bulk-scoring`  
**Completed**: 2026-10-14  
**Status**: **PASS / T098 complete**

## Toolchain

- Selected .NET SDK: `10.0.402`
- Installed .NET 10 SDKs: `10.0.113`, `10.0.402`
- Selected .NET runtime: `Microsoft.NETCore.App 10.0.13`
- Selected ASP.NET Core runtime: `Microsoft.AspNetCore.App 10.0.13`

Version evidence was captured with:

```powershell
dotnet --version
dotnet --list-sdks
dotnet --list-runtimes
```

## Exact T098 matrix

| Command | Exit | Current result |
| --- | ---: | --- |
| `dotnet test dotnet/TalentMatch.slnx` | 0 | Domain: 71/71 passed in 2 s; Application: 347/347 passed in 2 s; Infrastructure: 412/412 passed in 1 m 36 s; Web: 389/389 passed in 1 m 42 s. Total: 1,219 passed, 0 failed, 0 skipped. |
| `dotnet build dotnet/TalentMatch.slnx -c Release` | 0 | Build succeeded with 66 warnings, 0 errors, elapsed 57.20 s. |
| `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkAuditContractTests|FullyQualifiedName~RequestAuthorizationStoreTests|FullyQualifiedName~BulkTerminalNotificationTests|FullyQualifiedName~BulkJobDeletionTests|FullyQualifiedName~BulkSourceOperationsTests"` | 0 | Application: 4/4 passed in 952 ms; Infrastructure: 94/94 passed in 7 s; Domain/Web correctly reported no matching tests. Total selected: 98 passed, 0 failed, 0 skipped. |

On PowerShell the VSTest OR operator is the unescaped `|` inside the quoted filter. The earlier
evidence rendered it as `\|`; passing those backslashes literally produces an invalid-filter
diagnostic and executes no selected tests even though `dotnet test` returns zero. The result above
uses the executable PowerShell form and verifies the intended 98 tests actually ran.

## Full-suite remediation

The initial exact rerun in this continuation exposed 116 failing cases:

- stale request/import/retention fixtures did not populate v3 frozen-limit columns;
- old migration and repository tests still asserted `same_tenant`, `cross_tenant`, owning-stack,
  permitted-root, and pre-v3 authorization behavior;
- Stack A's `server/storage/schema.sql` was incorrectly treated as if T100 had already unlocked its
  v3 migration; the schema file itself was not changed;
- mock endpoint and background tests bypassed the approved source-connection boundary;
- typed audit tests read the legacy processing stream instead of the detached v3 ledger;
- pre-v3 tests expected converted staging removal, generic `system` actors, and payloads without
  the required `factVersion`;
- two rubric fixture tests selected a copied build-output directory instead of the repository root;
  one user-management test expected unsafe raw server error text; and
- deterministic mock connection preview/retry and request-bound transfer exposed legitimate missing
  Stack B behavior, which was implemented rather than waived.

The final suite is green after:

- using fully bound delegated connection/request authorization in mock mode;
- adding deterministic mock source-connection preview and connection-bound retry behavior;
- preserving source-connection correlation through confirmation;
- fixing retry transfer ordering so the request, authorization, connection consumption, typed
  audits, and retry rows commit through the owning repository transaction;
- retaining original plus converted content under FR-010C;
- aligning frozen limits, provider model, actor, notification, problem-shape, configuration-version,
  and audit-fixture assertions with v3.1; and
- removing the superseded direct-preview dialog test file after its v3 source-connection UX
  replacement suite was already present and green.

## Expanded audit/provider evidence

The selected matrix verifies all closed `BulkAuditEventTypes` rows, actor rules, correlation, UTC
timestamps, safe versioned facts, prohibited-payload rejection, append-only behavior, detached
retention, relational rollback, durable handoff/reconciliation boundaries, authorization
establish/use/lapse/discard, terminal notification/acknowledgment, every content deletion kind, job
cleanup, and correction/reconciliation outcomes.

SQLite executes the mutation, cleanup, retention, rollback, and immutability scenarios. The
SQL Server provider used by Azure SQL generates and validates the matching detached envelope,
constraints, cascades, indexes, and safe-fact model. No live Azure SQL instance, tenant, Microsoft
login, consent, Graph, SharePoint, deployment, publisher verification, or release operation was
used by this automated gate.

Known NuGet package-advisory and obsolete-compatibility diagnostics remain warnings. They do not
change the zero-failure automated result and are not represented as remediated security advisories.

## Manual operator prerequisites after T098

T099 is now dependency-unblocked but remains a distinct manual live gate. Before an operator starts
it:

1. Use controlled commercial-cloud organizational test tenants and approved non-production source
   content. Prepare SharePoint, work/school OneDrive, another-tenant, shared-not-owned, duplicate,
   conversion-failure, lapse, cleanup, and reconciliation cases without placing names, URLs,
   identifiers, tokens, fingerprints, or content in checked-in evidence.
2. Complete the migration runbook: inventory old active work, drain/cancel it, discard protected
   old authorization without translation, apply the v3 EF migration, and verify completed history.
3. Configure an untracked environment with `MICROSOFT365_SOURCE_MODE=graph`, a controlled
   `PRODUCT_ENVIRONMENT`, `MICROSOFT365_SOURCE_CLIENT_ID`, the commercial `/organizations`
   authority, exact callback paths, delegated `openid profile offline_access Files.Read.All
   Sites.Read.All` scopes, Key Vault wrapping/admission-key settings, commercial Graph base URI,
   and the documented TTL/admission/discovery/byte/concurrency limits.
4. For Production, set `MICROSOFT365_SOURCE_PUBLISHER_VERIFIED=true`; for controlled Development/QA,
   the assertion may be absent/false but normal product/job authorization remains mandatory.
5. Verify the registration is `AzureADMultipleOrgs`, uses exact Web callbacks and reviewed managed
   identity federation, and has no client secret/certificate, SPA/public callback, Graph application
   role, `Sites.Selected`, source allow-list, or fallback reader.
6. Deploy only the prepared Stack B application/infrastructure through the reviewed operator
   process, verify migration and health, and then run the complete browser → API → worker T099
   journey. T099 must correlate sanitized live transitions to this automated matrix; it must not
   reuse historical app-only/OBO evidence.

## Gate decision

All executable T098 commands exit 0, all selected tests actually ran, and the expanded automated
SQLite/SQL Server-provider audit matrix is green. T098 is complete. This is not B-GATE PASS:
T099 remains unexecuted, and T100 still requires its distinct live evidence. No live operation was
attempted in T098.
