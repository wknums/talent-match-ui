# Stack B Security and Infrastructure Validation

**Feature**: `001-sharepoint-bulk-scoring`  
**Status**: T097 complete; all required validation commands pass.

## T087 intentional red — FR-021 job deletion

**Captured**: 2026-10-14  
**Command**:

```powershell
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkJobDeletionTests" --logger "console;verbosity=minimal"
```

**Result**: Exit 1 as required before T096 implementation. The Infrastructure test assembly ran
six tests and reported `Failed: 6, Passed: 0, Skipped: 0, Total: 6`. Failures were the intended
missing FR-021 behavior rather than fixture or compilation failures, including:

- no detached `bulk_job_cleanup_completed` history after deletion;
- no required original/converted/transient, item/request correction, and reconciliation events;
- partial staged-content cleanup incorrectly reported success instead of recording a durable
  pending reconciliation decision;
- no append-only surviving cleanup audit row;
- incomplete provider-model parity assertions.

Warnings about the already-selected package versions were emitted during restore/build and are not
accepted as satisfying or waiving any T087 assertion.

## Final T097 validation

**Completed**: 2026-10-14  
**Status**: **PASS**. The operator-authorized removal of the sole trailing blank line at EOF in
`server/storage/schema.sql` cleared the previous repository-wide whitespace blocker. All other
bytes in that file were preserved: the 56,751-byte post-edit file is the exact 56,751-byte prefix
of the 56,753-byte pre-edit file, and the only removed bytes were one terminal CRLF (`0d0a`).

| Command | Exit | Result |
| --- | ---: | --- |
| `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkAuditContractTests\|FullyQualifiedName~RequestAuthorizationStoreTests\|FullyQualifiedName~BulkTerminalNotificationTests\|FullyQualifiedName~BulkJobDeletionTests\|FullyQualifiedName~BulkSourceOperationsTests"` | 0 | Application: 4 passed; Infrastructure: 94 passed; Domain/Web had no matching tests |
| `node infra/terraform/tests/assert-source-connector.mjs` | 0 | One delegated-only connector across 45 Terraform files |
| `node infra/terraform/tests/assert-no-legacy-sharepoint-access.mjs` | 0 | 55 infrastructure files contain no legacy reader/OBO/app-only/`Sites.Selected`/source allow-list/site-grant path |
| `terraform fmt -check -recursive infra/terraform` | 0 | Formatting check passed |
| `terraform -chdir=infra/terraform/live/shared validate` | 0 | Configuration valid |
| `terraform -chdir=infra/terraform/live/stack-b validate` | 0 | Configuration valid |
| `pwsh -NoProfile -File infra/terraform/tests/validate-stack-b-roots.ps1` | 0 | Deterministically selected and validated `live/shared` and `live/stack-b` |
| `git diff --check` | 0 | No whitespace errors; Git emitted only line-ending conversion warnings for existing dirty files |

The root-validation output named both required roots, validated each, and reported:
`PASS: shared and all discovered Stack B Terraform roots validated.` Its implementation rejects a
missing required `live/shared` or `live/stack-b` root and propagates a non-zero validation result
from any deterministically discovered Stack B root.

Operational-document review verified that
`docs/SHAREPOINT_BULK_SCORING_APP_REGISTRATION.md`, `README.md`, `AUTHENTICATION.md`, and
`INTEGRATION.md` present the activated Stack B path as the separate confidential
`AzureADMultipleOrgs` authorization-code + PKCE connector with delegated Graph scopes and
encrypted request-bound authorization. References to application-only readers, `Sites.Selected`,
site grants, source allow-lists, OBO exchange, passwords/certificates, same/cross-tenant selectors,
or fallback identities are explicit statements that those paths do not exist; they are not
presented as current Stack B setup or operating instructions. The unrelated phrase “app-only ZIP
deployment script” in `INTEGRATION.md` describes a packaging script, not SharePoint/Graph access.

Known restore/build warnings report existing package advisories for the selected package versions:
`Microsoft.AspNetCore.Components.WebAssembly.Server`, `Microsoft.AspNetCore.SignalR.Common`, and
`System.Security.Cryptography.Pkcs`. They did not fail the focused test command and are not treated
as satisfying any gate.
