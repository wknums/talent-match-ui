# Live run of Stack B against a real SharePoint folder (cross-tenant, certificate)

**Date**: 2026-10-06. **Part of**: T127 (manual real-tenant acceptance), partial. **Stack**: B only.

## Setup

- Stack B web host run locally (`ASPNETCORE_ENVIRONMENT=Testing`, simple sign-in, a temporary SQLite database that was deleted afterwards).
- `SHAREPOINT_MODE=graph` with the cross-tenant app and its **certificate** (the local-development path), against the synthetic test site in the other tenant. The folder holds 68 synthetic CV files: 67 Markdown and 1 Word document.
- Driven through the public API, as the UI does: preview, confirm, status polling, items, original download, cancel, retry.
- Not covered here: the UI in a browser, the same-tenant (delegated) path, OneDrive, large folders and discovery steps (the real folder is small), the federated credential from Azure (S4).

## Results

| Step | Result |
| --- | --- |
| Capabilities | `graph` mode, `cross_tenant`, SharePoint only |
| Preview | 68 files found in one step, 0 excluded, 0 unreadable, discovery `complete` |
| Confirm with "convert to image PDF" | Accepted at once (`active`), no authorization needed in this mode |
| Background import | All 68 files imported, converted and handed to scoring in about **60 seconds**, 0 failures, 4 files at a time. The Word file was converted by SharePoint and then rasterized locally |
| Items | 68 `scoring`, scored version `converted` for all, 1–2 pages each, about 159 KB converted size on average, 68 originals retained |
| Original download | `200`, `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff` |
| Cancel right after confirm | Request `completed` with outcome `cancelled`, all 68 files `cancelled`, nothing left importing |
| Retry failed on that request | A new request with 68 files, `retryOfRequestId` pointing at the cancelled one; all 68 imported and handed to scoring, 0 failures. The new request took the files' current eTags as designed |
| Notification | The cancelled request appears in the notifications list until acknowledged |

## Findings

1. **The bulk tables are not in the shared database schema yet (T074).** The web host only creates tables in `Testing` from the EF model. In Development/QA/production it initializes the database from `server\storage\schema-sqlite.sql` and `schema.sql`, which do not contain the five bulk tables, and the host does not run the EF migration. Until T074 adds them (it is currently listed after the B-GATE), Stack B cannot run this feature outside tests. **Update 2026-10-12:** the Azure SQL file (`schema.sql`) is done and verified on SQL Server; SQLite and Stack A are not.
2. **Running the host in Development with `SQLITE_DB_PATH` set used the shared development database anyway.** `ConnectionStrings:DefaultConnection` in the development settings takes precedence over `SQLITE_DB_PATH`. The run failed during the host's usual schema evolution of `shared-data\talentmatch.db` (`no such table: PromptGenerationInstructions`), after it had already applied its usual additive steps. No data was read or written by the bulk feature. Use `ConnectionStrings__DefaultConnection` for a temporary database.

## Deployed QA app (2026-10-12)

Stack B (`app-talent-blazor-test`), shared Azure SQL, cross-tenant, **managed identity** (no certificate, password or secret on the host), recruiter driving the browser UI.

| Step | Result |
| --- | --- |
| Deploy | Settings applied through `deploy.sh` (five non-secret `SHAREPOINT_*` settings); build from `package-stack-b.sh`; the new endpoints answer `401` instead of the SPA page |
| Bulk tables on Azure SQL | Created by the host's background schema initialization; the worker's first poll failed three times with "Invalid object name" within one second, then recovered |
| First preview | **500** — `AADSTS700236`: Entra refused the federated credential that sat on the reader app registered in the SharePoint tenant (4 failures in that tenant's sign-in log). Design corrected: multi-tenant reader app in the home tenant, consent and `read` grant in the SharePoint tenant |
| Second preview after the setting changed | Still the old client id: the web app had not picked the new setting up; a restart fixed it |
| Preview and bulk request of the 68-file folder | Worked from the browser; recruiter confirmed jobs running |
| Closing the browser while jobs run | Not yet recorded (jobs were still running when this was written) |

Follow-ups made from this run: the worker now waits for the Azure SQL schema initialization (no start-up errors), SQL command logging is at Warning (it was rotating the container log every ~45 s), and the cross-tenant reader is in Terraform.
