# Cross-Stack Parity Checklist: Delete Jobs and Enhanced Job Cards

**Purpose**: Verify that Stack A (Node.js/Express + React/TypeScript) and Stack B (.NET Blazor WASM / Clean Architecture) implement equivalent functionality for every acceptance scenario  
**Created**: March 11, 2026  
**Feature**: [spec.md](../spec.md)  
**Focus**: Cross-stack behavioral parity  
**Depth**: Standard  
**Audience**: Reviewer (PR / implementation verification)

**Stack Status**:

- Stack B (.NET Blazor): delete flow and enhanced card metadata are implemented end-to-end
- Stack A (Node.js/React): delete flow and enhanced card metadata are now implemented; remaining checklist work is verification plus one response-shape parity follow-up on `GET /api/jobs`

---

## US1 — Admin Deletes a Job from Dashboard

- [x] CHK001 Both stacks expose a `DELETE /api/jobs/:jobId` endpoint that returns 204/success on valid admin request [Spec §FR-001, FR-005]
- [x] CHK002 Both stacks return 404 when the target job does not exist [Spec §FR-010]
- [x] CHK003 Both stacks show a confirmation dialog displaying the job title before deletion [Spec §FR-003]
- [x] CHK004 Both stacks optimistically remove the job card from the Dashboard without a full page reload after confirmed deletion [Spec §FR-008]
- [x] CHK005 Both stacks re-insert the job card and show an error toast if the delete API call fails [Spec §FR-010]
- [x] CHK006 Both stacks cancel deletion and close the dialog when the user clicks Cancel [Spec US1-AS3]
- [x] CHK007 Both stacks hide the delete button from non-admin users on Dashboard job cards [Spec §FR-006, US1-AS4]

## US2 — Admin Deletes a Job with Applications

- [x] CHK008 Both stacks show a cascade warning in the confirmation dialog when the job has ≥1 application, including the exact application count [Spec §FR-004, US2-AS1]
- [x] CHK009 Both stacks cascade-delete all associated applications, scoring data, and config versions when deletion is confirmed [Spec §FR-005, US2-AS2]
- [x] CHK010 Both stacks suppress the cascade warning when the job has 0 applications (simple confirmation only) [Spec Edge Cases]

## US3 — Admin Deletes a Job from Detail Page

- [x] CHK011 Both stacks render a delete button on the Job Detail page, visible only to admin users [Spec §FR-002, US3-AS1]
- [x] CHK012 Both stacks show the same confirmation dialog (with cascade warning if applicable) when deleting from the Detail page [Spec US3-AS2]
- [x] CHK013 Both stacks redirect to the Dashboard after successful deletion from the Detail page [Spec §FR-009, US3-AS3]
- [x] CHK014 Both stacks show an error toast and remain on the Detail page if deletion fails [Spec §FR-010]

## US4 — Enhanced Cards: Creator Name

- [x] CHK015 Both stacks resolve `createdBy` to a human-readable full name displayed on the job card [Spec §FR-012, US4-AS1]
- [x] CHK016 Both stacks display "Unknown User" (or equivalent) when the creator's user record is unavailable or deleted [Spec §FR-013, US4-AS2]
- [x] CHK017 Both stacks resolve the name server-side (Stack B: left-join in query handler; Stack A: user map lookup in GET handler) so the client receives `createdByName` directly [Plan parity]

## US5 — Enhanced Cards: Creation Date

- [x] CHK018 Both stacks display the creation date on the job card in a human-friendly format (e.g., "Created Mar 5, 2026") [Spec §FR-011, US5-AS1]
- [x] CHK019 Both stacks format the date consistently (locale-appropriate short month, day, full year) [Spec §FR-011]

## US6 — Enhanced Cards: Completion Status

- [x] CHK020 Both stacks display "X / Y completed" on the job card when `totalApplications > 0` [Spec §FR-014, US6-AS1]
- [x] CHK021 Both stacks display "0 applications" (or omit the indicator) when `totalApplications == 0` [Spec US6-AS2]
- [x] CHK022 Both stacks compute completion counts server-side and include them in the GET /api/jobs response [Contract parity]

## Authorization

- [x] CHK023 Both stacks enforce admin-only access on the delete endpoint via middleware/policy (Stack B: `RequireAuthorization("AdminOnly")`; Stack A: `requireRole('admin')`) [Spec §FR-006]
- [x] CHK024 Both stacks return 403 (Forbidden) when a non-admin user calls the delete endpoint directly [Spec §FR-007]
- [x] CHK025 Both stacks hide all delete UI controls (buttons, icons) from non-admin users on both Dashboard and Detail pages [Spec §FR-006, SC-002]
- [x] CHK026 Both stacks preserve all existing job card information (title, organisation, department, days posted, status badge) alongside new fields [Spec §FR-015]

## Edge Cases

- [x] CHK027 Both stacks handle concurrent deletion gracefully — second delete attempt returns 404, UI shows appropriate feedback [Spec Edge Cases]
- [x] CHK028 Both stacks handle network errors during deletion — error toast is displayed, job card is restored to the list [Spec §FR-010, Edge Cases]
- [x] CHK029 Both stacks handle jobs with 0 applications — no cascade warning shown, simple confirmation only [Spec Edge Cases]
- [x] CHK030 Both stacks handle deleted/unavailable creator — "Unknown User" fallback renders without error [Spec §FR-013, Edge Cases]
- [ ] CHK031 Both stacks handle jobs with large application counts (500+) — warning still shows count, no UI timeout [Spec Edge Cases]
  - Pending explicit stress validation; the implementation appears correct, but this case was not exercised in runtime verification.

## API Contract Parity

- [ ] CHK032 Both stacks' `GET /api/jobs` response includes matching fields: `createdByName`, `createdAt`, `totalApplications`, `completedApplications` (or equivalent names) [Contract: contracts/get-jobs.md]
  - Stack A now returns `createdByName` and `createdAt`, but application counts are still nested under `stats.totalApplications` / `stats.completed` rather than top-level fields.
- [x] CHK033 Both stacks' `DELETE /api/jobs/:jobId` returns the same success/error status codes (204/success, 404, 403) [Contract: contracts/delete-job.md]

## Notes

- Check items off as verified: `[x]`
- Stack B is no longer the only implemented stack for this feature; Stack A now has the delete flow and enhanced card/detail metadata as well
- Items are numbered CHK001–CHK033 for cross-reference in reviews
- Add inline comments for any intentional divergences between stacks
- Current review outcome: 31/33 items are now code-verified. The remaining open items are large-count runtime validation and Stack A response-shape alignment for `GET /api/jobs`.
