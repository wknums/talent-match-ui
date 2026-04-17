# Cross-Stack Parity Dashboard

**Purpose**: Track implementation parity between Stack A (Node.js/Express + React/TypeScript) and Stack B (.NET Blazor WASM / Clean Architecture) across all features.  
**Last Updated**: April 17, 2026  
**Constitution**: v1.1.1 — "New features SHOULD be planned for both Stack A and Stack B."

---

## Overview

| Feature | Stack B (.NET) | Stack A (React/Express) | Parity Status | Checklist |
| ------- | ------------- | ---------------------- | ------------- | --------- |
| 001 — Talent Matching Platform | 🔶 Mostly complete | ✅ Complete | 🔶 Re-review required | [parity.md](specs/001-talent-matching-platform/checklists/parity.md) |
| 002 — Edit User Departments | ✅ Implemented | ✅ Complete | 🔶 Re-review required | [parity.md](specs/002-edit-user-departments/checklists/parity.md) |
| 003 — Delete Jobs + Enhanced Cards | ✅ Implemented | ✅ Implemented | 🔶 Re-review required | [parity.md](specs/003-delete-jobs-enhanced-cards/checklists/parity.md) |
| 005 — Recruiter Analytics | ✅ Complete | ✅ Implemented | 🔶 Re-review required | No parity checklist yet |

### Legend

- ✅ **Verified** — Both stacks implement equivalent behavior; all parity checks pass
- 🔶 **In Progress** — One or both stacks have incomplete work; parity not yet verifiable
- ⬜ **Not Assessed** — No parity checklist exists yet, or implementation exists but has not been parity-verified
- ❌ **Diverged** — Known behavioral differences between stacks requiring resolution

---

## Feature Details

### 001 — Talent Matching Platform

- **Status**: 🔶 **Near parity, but not fully verified**
- **Stack B**: Core auth, dashboard, job, rubric extraction, prompt, analytics, and manual-review flows are present. The main remaining parity blocker is still password-reset request listing: [UsersEndpoints.cs](dotnet/src/Web.Server/Endpoints/UsersEndpoints.cs) exposes request-create and resolve actions, but `GET /api/users/reset-requests` still returns an empty placeholder list, so the admin approval workflow is not parity-complete end-to-end.
- **Stack A**: Core platform flows remain implemented end-to-end, including reset-request listing and resolution in [users.ts](server/routes/users.ts), rubric extraction in [jobs.ts](server/routes/jobs.ts), and manual review in [ManualReviewView.tsx](src/components/ManualReviewView.tsx).
- **Parity Checklist**: [Checklist file exists](specs/001-talent-matching-platform/checklists/parity.md)
  - Current checklist summary is 57/59, not 58/59.
  - The open item is still the recruiter reset-request approval workflow on Stack B.
  - US3 Job Creation & Extraction: 8/8 ✅
  - US3a Prompt Management: 7/7 ✅
  - US4 Application Upload: 4/4 ✅
  - US5 Scoring Pipeline: 6/6 ✅
  - US6 Ranked Lists & Detail: 4/4 ✅
  - US7 Manual Review: 4/4 ✅
  - US8 Monitoring & Pipeline: 5/5 ✅
  - Authorization & Security: 4/4 ✅
- **Important Note**: The linked checklist is directionally correct, but its prior summary/commentary overstated Stack B user-management parity. Treat this feature as needing re-verification rather than fully verified.
- **Critical Note**: The `tasks.md` still marks the original feature work as unchecked, but the actual implementation state is far ahead of the task file.

### 002 — Edit User Departments

- **Status**: 🔶 **Code looks parity-complete, but the checklist needs to be rerun**
- **Stack B**: The missing pieces called out in the old dashboard are now present. [UsersEndpoints.cs](dotnet/src/Web.Server/Endpoints/UsersEndpoints.cs) exposes `PUT /api/users/{userId}`, and [UserManagement.razor](dotnet/src/Web.Client/Components/UserManagement.razor) includes a pre-populated edit modal, multi-department tag editing, self-role-change lock, inline save/cancel flow, and department badge rendering in the grid.
- **Stack A**: The equivalent backend and dialog flow remain implemented in [users.ts](server/routes/users.ts) and [UserManagementDialog.tsx](src/components/UserManagementDialog.tsx), including comma validation, department tag editing, and self-role preservation.
- **Parity Checklist**: [Checklist file exists](specs/002-edit-user-departments/checklists/parity.md)
  - The note at the bottom of the checklist is now consistent with the code: Stack B appears implemented.
  - The checklist items are still unchecked, so this feature should be treated as "ready for re-verification" rather than formally verified.

### 003 — Delete Jobs + Enhanced Cards

- **Status**: 🔶 **Both stacks are implemented; parity verification now has a smaller follow-up surface**
- **Stack B**: The full flow is present end-to-end. [JobsEndpoints.cs](dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs) exposes `DELETE /api/jobs/{jobId}` behind `AdminOnly`; [Dashboard.razor](dotnet/src/Web.Client/Pages/Dashboard.razor) and [JobDetail.razor](dotnet/src/Web.Client/Pages/JobDetail.razor) both show admin-only delete actions with a confirmation dialog; and [GetJobSummariesQuery.cs](dotnet/src/Application/Jobs/Queries/GetJobSummariesQuery.cs) supplies `CreatedByName`, `TotalApplications`, and `CompletedApplications` for enriched cards.
- **Stack A**: The previously missing pieces are now present. [jobs.ts](server/routes/jobs.ts) enriches `GET /api/jobs` with `createdByName` and exposes `DELETE /api/jobs/:jobId`; [job-repo.ts](server/storage/repos/job-repo.ts) performs transactional deletion with cleanup for non-cascaded rows; [DeleteJobDialog.tsx](src/components/DeleteJobDialog.tsx), [DashboardView.tsx](src/components/DashboardView.tsx), [JobCard.tsx](src/components/JobCard.tsx), and [JobDetailView.tsx](src/components/JobDetailView.tsx) now provide admin-only delete actions, confirmation dialogs, creator attribution, creation dates, and completion status rendering.
- **Parity Checklist**: [Checklist file exists](specs/003-delete-jobs-enhanced-cards/checklists/parity.md)
  - The checklist now reflects implementation on both stacks.
  - 31/33 items are code-verified.
  - Remaining open items are: explicit large-application-count runtime validation, and Stack A response-shape alignment for `GET /api/jobs` application counts.

### 005 — Recruiter Analytics

- **Status**: 🔶 **Both stacks are implemented; parity now needs a real verification pass**
- **Stack B**: Analytics remains complete from API to UI, including [AnalyticsEndpoints.cs](dotnet/src/Web.Server/Endpoints/AnalyticsEndpoints.cs), the query handlers under [Application/Analytics](dotnet/src/Application/Analytics), the navigation button in [Dashboard.razor](dotnet/src/Web.Client/Pages/Dashboard.razor), and the page in [Analytics.razor](dotnet/src/Web.Client/Pages/Analytics.razor).
- **Stack A**: The old recruiter-ownership bug is no longer current. [jobs.ts](server/routes/jobs.ts) now stores `createdBy` as `req.user?.userId`, and [analytics.ts](server/services/analytics.ts) resolves recruiter ownership by exact user ID first, then falls back to username and finally to a single-user department heuristic. The UI is wired through [AnalyticsView.tsx](src/components/AnalyticsView.tsx) and [api-real.ts](src/lib/api-real.ts).
- **Parity Checklist**: No parity checklist has been generated yet for this feature. The next step is verification, not bug-fixing.

---

## Cross-Cutting Observations

- **Feature 002 has overtaken the dashboard**: The previous claim that Stack B lacked the endpoint and UI is now stale; the remaining work is checklist verification.
- **Feature 003 has overtaken the dashboard**: Stack A now has the delete flow and enhanced cards; the remaining work is parity verification plus a smaller response-contract cleanup on `GET /api/jobs`.
- **Feature 005 has overtaken the dashboard**: The previously documented Stack A recruiter-ownership mismatch is fixed in the current code path.
- **Manual review and rubric upload are present in both stacks**: Stack A uses [ManualReviewView.tsx](src/components/ManualReviewView.tsx) and [UploadRubricDialog.tsx](src/components/UploadRubricDialog.tsx); Stack B uses [ManualReview.razor](dotnet/src/Web.Client/Pages/ManualReview.razor), `ExtractRubricAsync()` in [ApiClient.cs](dotnet/src/Web.Client/Services/ApiClient.cs), and rubric upload in [CreateJobDialog.razor](dotnet/src/Web.Client/Components/CreateJobDialog.razor). This should not be treated as a one-stack-only divergence.
- **Shared local development storage is now closer than before**: Both stacks target the shared SQLite path in local development, so the parity dashboard no longer needs to treat local storage shape as the main cross-stack blocker.

---

## Non-Parity-Scoped Specs

### 004 — Edit Job UI for Stack B

- **Scope**: Stack B-only parity catch-up feature.
- **Current State**: All tasks T001–T019 are complete in [tasks.md](specs/004-edit-job-stack-b/tasks.md).
- **Why not in the parity table**: This feature exists to bring Stack B up to behavior Stack A already had; it is not tracked as a standalone cross-stack checklist feature.

### 006 — Selective Azure Deployment

- **Scope**: Deployment and infrastructure planning feature, not an application-surface parity feature.
- **Current State**: Planning artifacts now exist through [spec.md](specs/006-selective-azure-deploy/spec.md), [plan.md](specs/006-selective-azure-deploy/plan.md), [research.md](specs/006-selective-azure-deploy/research.md), [data-model.md](specs/006-selective-azure-deploy/data-model.md), and [tasks.md](specs/006-selective-azure-deploy/tasks.md). No parity checklist is tracked because this is still infrastructure-scope work.
- **Parity relevance**: This may later need deployment-scope parity guidance, but it is not currently tracked in the cross-stack feature dashboard.

---

## Priority Actions

1. **Feature 001 — Finish Stack B reset-request listing**: `GET /api/users/reset-requests` still returns a placeholder empty list, which is the clearest remaining blocker to feature 001 parity.
2. **Feature 003 — Align Stack A `GET /api/jobs` response shape**: Stack A now implements the feature, but still returns application counts under `stats` instead of the top-level fields described by the feature contract.
3. **Feature 002 — Rerun the parity checklist**: The code now appears implemented in both stacks, but the checklist has not been re-walked item by item.
4. **Feature 005 — Generate and run a parity checklist**: Both stacks now expose analytics endpoints and views; this feature needs verification rather than more speculative dashboard notes.
5. **Reconcile stale checklist/task metadata**: Features 001, 002, and 003 still have checklist/task commentary that lags the actual implementation state.

---

## Process

### When to Update This Dashboard

- After completing a parity checklist verification round
- After finishing all Stack A or Stack B tasks for a feature
- After running `/speckit.checklist` to generate a new parity checklist
- When a known divergence is identified between stacks

### How to Create a Parity Checklist

1. Run `/speckit.checklist` with focus on "cross-stack parity" for the target feature
2. Save output to `specs/<feature>/checklists/parity.md`
3. Update this dashboard's Overview table with the new checklist link and counts
4. Verify items as both stacks complete implementation

### Handling Divergences

- If a parity check fails, add an inline note to the checklist item explaining the divergence
- Mark the feature as ❌ **Diverged** in the Overview table
- Create a follow-up task or spec to resolve the divergence
- Once resolved, re-verify and update status to ✅ **Verified**
