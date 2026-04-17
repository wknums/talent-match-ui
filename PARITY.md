# Cross-Stack Parity Dashboard

**Purpose**: Track implementation parity between Stack A (Node.js/Express + React/TypeScript) and Stack B (.NET Blazor WASM / Clean Architecture) across all features.  
**Last Updated**: April 16, 2026  
**Constitution**: v1.1.1 — "New features SHOULD be planned for both Stack A and Stack B."

---

## Overview

| Feature | Stack B (.NET) | Stack A (React/Express) | Parity Status | Checklist |
| ------- | ------------- | ---------------------- | ------------- | --------- |
| 001 — Talent Matching Platform | 🔶 Mostly complete | ✅ Complete | 🔶 Re-review required | [parity.md](specs/001-talent-matching-platform/checklists/parity.md) |
| 002 — Edit User Departments | 🔶 Partial (backend pieces exist) | ✅ Complete | ❌ Diverged | [parity.md](specs/002-edit-user-departments/checklists/parity.md) |
| 003 — Delete Jobs + Enhanced Cards | ✅ Implemented | ⬜ Not implemented | ❌ Diverged | [parity.md](specs/003-delete-jobs-enhanced-cards/checklists/parity.md) |
| 005 — Recruiter Analytics | ✅ Complete | 🔶 Implemented with data bug | ❌ Diverged | No parity checklist yet |

### Legend

- ✅ **Verified** — Both stacks implement equivalent behavior; all parity checks pass
- 🔶 **In Progress** — One or both stacks have incomplete work; parity not yet verifiable
- ⬜ **Not Assessed** — No parity checklist exists yet, or implementation exists but has not been parity-verified
- ❌ **Diverged** — Known behavioral differences between stacks requiring resolution

---

## Feature Details

### 001 — Talent Matching Platform

- **Status**: 🔶 **Near parity, but not fully verified**
- **Stack B**: Core auth, dashboard, job, scoring, prompt, and review flows are present, but the password-reset request listing path is not parity-complete end-to-end. The current web endpoint returns an empty list for reset requests, so the recruiter/admin approval workflow cannot be treated as fully verified.
- **Stack A**: Core platform flows are implemented end-to-end, including password-reset request listing and resolution behavior.
- **Parity Checklist**: [Checklist file exists](specs/001-talent-matching-platform/checklists/parity.md)
  - US1 Authentication & Dashboard: 7/7 ✅
  - US2 User Management: 6/6 ✅
  - US3 Job Creation & Extraction: 8/8 ✅
  - US3a Prompt Management: 7/7 ✅
  - US4 Application Upload: 4/4 ✅
  - US5 Scoring Pipeline: 6/6 ✅
  - US6 Ranked Lists & Detail: 4/4 ✅
  - US7 Manual Review: 4/4 ✅
  - US8 Monitoring & Pipeline: 5/5 ✅
  - Authorization & Security: 4/4 ✅
  - Infrastructure & Storage: CHK059 is now marked checked in the checklist body
- **Important Note**: The linked checklist is stale in two ways: its summary still reports 58/59, and its user-management section overstates current Stack B parity. Treat this feature as needing re-verification rather than fully verified.
- **Critical Note**: The `tasks.md` still marks the original feature work as unchecked, but the actual implementation state is far ahead of the task file.

### 002 — Edit User Departments

- **Status**: ❌ **Stack B web-surface parity is incomplete**
- **Stack B**: The Application-layer update command and validator exist, but the current web surface does not match the parity claim. There is no exposed user-update endpoint, and the Blazor user-management UI does not currently provide the edit-user and multi-department editing flow this feature requires.
- **Stack A**: Backend update behavior and the user-management edit flow are implemented, including the department editing experience.
- **Parity Checklist**: [23/23 items previously verified](specs/002-edit-user-departments/checklists/parity.md)
  - The checklist result is now stale relative to the code and should be rerun after Stack B exposes the missing endpoint/UI behavior.

### 003 — Delete Jobs + Enhanced Cards

- **Status**: ❌ **Stack B is implemented; Stack A is still missing the feature**
- **Stack B**: The delete endpoint, admin-only dashboard/detail delete actions, confirmation dialog with cascade warning, creator-name display, created-at display, and application completion counts are implemented end-to-end. Remaining task items are polish/verification only.
- **Stack A**: The delete API, repository support, dashboard/detail delete actions, and enhanced card fields are still not implemented.
- **Parity Checklist**: [Checklist file exists](specs/003-delete-jobs-enhanced-cards/checklists/parity.md)
  - Treat the checklist as a backlog/verification template. It has not been updated to reflect that Stack B is already functionally implemented while Stack A remains pending.

### 005 — Recruiter Analytics

- **Status**: ❌ **Stack A analytics is wired, but the recruiter rollups are internally inconsistent**
- **Stack B**: T001–T029 complete, including analytics endpoints, Blazor analytics page, role-based navigation, recruiter and department views, and test coverage.
- **Stack A**: The analytics UI and endpoints are wired, but recruiter ownership semantics do not line up. Job creation stores `createdBy` as username, while recruiter analytics groups jobs by recruiter `userId`; recruiter-level counts can therefore miss owned jobs, and department rollups inherit the same error.
- **Parity Checklist**: No parity checklist has been generated yet for this feature. Fix Stack A's data-mapping issue before creating one.

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

1. **Feature 003 — Implement Stack A**: The current divergence is largest here. Stack B already has the feature; Stack A still needs the delete flow and enhanced cards.
2. **Feature 005 — Fix Stack A recruiter ownership mapping**: Align `createdBy` semantics so recruiter analytics rollups are trustworthy before generating a parity checklist.
3. **Feature 002 — Finish Stack B edit-user parity**: Expose the missing update endpoint and matching edit UI, then rerun the parity checklist.
4. **Feature 001 — Re-verify user-management parity**: The checklist summary is stale, and the current Stack B reset-request listing path prevents treating the feature as fully verified.
5. **Reconcile stale checklist/task metadata**: Features 001, 002, and 003 all have checklist/task files that no longer accurately describe current implementation state.

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
