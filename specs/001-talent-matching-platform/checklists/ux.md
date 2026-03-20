# UX Requirements Quality Checklist — 001 Talent Matching Platform

**Purpose**: Validate the completeness, clarity, consistency, and coverage of UX/UI-related requirements across spec.md, plan.md, and data-model.md — focusing on workflow design, interaction patterns, visual specifications, responsive behaviour, and accessibility.
**Created**: March 20, 2026
**Feature**: [spec.md](../spec.md)
**Depth**: Standard
**Audience**: Reviewer (PR)
**Focus Areas**: Multi-step workflow design, interaction state specifications, document rendering, responsive layout, accessibility, error/empty state UX, information architecture

---

## Requirement Completeness

- [ ] CHK001 — Are loading/skeleton state requirements defined for each of the ~10 major views (Dashboard, Job Detail, Application Detail, Manual Review, Prompt Management, User Management, Failure Queue, Lists, Create Job, Upload)? [Gap]
- [ ] CHK002 — Are empty-state requirements specified for all list/table views — zero jobs on dashboard, zero applications on a job, zero prompt revisions, zero failure queue items, zero test runs? [Gap, Spec §US8, §US3a, §US6]
- [ ] CHK003 — Are success/confirmation feedback requirements defined for all mutating operations beyond "visible success message" (FR-030) — e.g., toast duration, placement, dismissal behaviour? [Gap, Spec §FR-030]
- [ ] CHK004 — Are pagination requirements specified for all large-data views — application lists (up to 20K items), scoring run history, audit trail entries, prompt revision lists? [Gap, Spec §FR-016]
- [ ] CHK005 — Are keyboard shortcut or accelerator requirements defined for high-frequency recruiter workflows (e.g., navigating between applications in manual review, saving reviews)? [Gap, Spec §US7]
- [ ] CHK006 — Are breadcrumb or navigation trail requirements defined for deep-nested views (Dashboard → Job → Application → Manual Review) to prevent user disorientation? [Gap]
- [ ] CHK007 — Are undo/cancel requirements specified for destructive or multi-step operations (e.g., cancelling a prompt approval, abandoning a partially-filled job creation form)? [Gap]
- [ ] CHK008 — Are drag-and-drop interaction requirements for bulk upload (US4) specified with fallback for non-mouse users (keyboard, touch)? [Completeness, Spec §US4 scenario 1]
- [ ] CHK009 — Are tooltip or contextual help requirements defined for domain-specific UI concepts (variance threshold, aggregation strategy, must-have vs desired criteria, prompt versioning)? [Gap]
- [ ] CHK010 — Are date/time display format requirements specified — timezone handling, relative vs absolute timestamps, locale formatting? [Gap]
- [ ] CHK011 — Are requirements defined for the prompt editor (US3a scenario 4/8) — syntax highlighting, line numbers, word wrap, minimum height, character/line limits? [Gap, Spec §US3a]
- [ ] CHK012 — Are visual requirements specified for the prompt rating system (FR-037) — star display, hover interaction, inline vs modal editing of rating/comments? [Gap, Spec §FR-037]

---

## Requirement Clarity

- [ ] CHK013 — Is "three resizable panes" (US7) quantified with initial size ratios, minimum pane widths, drag handle affordances, and collapse/expand behaviour? [Clarity, Spec §US7 scenario 1]
- [ ] CHK014 — Is "progress indicator per file" (US4 scenario 4) clarified — individual progress bars, a single aggregate bar, percentage vs bytes, and behaviour when files complete at different rates? [Ambiguity, Spec §US4 scenario 4]
- [ ] CHK015 — Is "filterable job list" (US8 scenario 2) clarified — which fields are filterable, is it free-text search, dropdown filters, or both? Spec only mentions department and organisation. [Clarity, Spec §US8 scenario 2]
- [ ] CHK016 — Is "sortable columns" (FR-016) clarified for longlist/shortlist/exclusion views — which columns are sortable, default sort order, and multi-column sort capability? [Clarity, Spec §FR-016]
- [ ] CHK017 — Is "live score updates immediately" (US7 scenario 2) clarified — does this mean per-keystroke, on-blur, or on-field-commit recalculation? [Ambiguity, Spec §US7 scenario 2]
- [ ] CHK018 — Is "expandable results summary dialog" (US3a scenario 13 / FR-052) specified with layout details — table vs card layout, scrollable content for many test applications, column definitions? [Clarity, Spec §FR-052]
- [ ] CHK019 — Is "error inline" for invalid upload files (US4 scenario 1) clarified — inline within the file list, a toast notification, a banner, or a per-file error icon with tooltip? [Ambiguity, Spec §US4 scenario 1]
- [ ] CHK020 — Is "prompt management controls" (US3a scenario 1) clarified with specific UI element types — buttons, dropdown menu, toolbar, or contextual actions? [Clarity, Spec §US3a scenario 1]
- [ ] CHK021 — Is "disabled with a message" for prompt controls when no job exists (US3a scenario 2) specified — tooltip on hover, inline text, or modal explanation? [Ambiguity, Spec §US3a scenario 2]
- [ ] CHK022 — Is "pipeline visualiser" (US8 scenario 4) defined beyond stage names — visual representation (progress bar, Kanban, flowchart), interactivity (clickable stages), and real-time update mechanism? [Clarity, Spec §US8 scenario 4]
- [ ] CHK023 — Is "coloured border" or status-based visual differentiation for job cards defined with specific colour mappings per job status? [Clarity, Plan §Project Structure reference to JobCard.tsx]

---

## Requirement Consistency

- [ ] CHK024 — Are document rendering requirements consistent between the Manual Review left pane (US7/FR-058), Application Detail view (FR-025/US6 scenario 4), and test-case review — do all three use the same rendering pipeline for the same document types? [Consistency, Spec §US7, §FR-025, §FR-058]
- [ ] CHK025 — Are auto-refresh requirements consistent across views — Dashboard at 30s (FR-015), Failure Queue at 30s (FR-027), and test-run status polling — are refresh intervals aligned and configurable? [Consistency, Spec §FR-015, §FR-027, §FR-048]
- [ ] CHK026 — Are dialog/modal patterns consistent across the application — Create Job dialog, Upload dialog, Change Password dialog, Results Summary dialog, User Management — do they share consistent close/cancel/save patterns? [Consistency, Spec §FR-024, §FR-028, §FR-052]
- [ ] CHK027 — Are table/list component patterns consistent between the job list (dashboard), application lists (US6), user management list (US2), failure queue list (US8), and prompt revision list (US3a)? [Consistency]
- [ ] CHK028 — Are error message display patterns consistent — FR-030 specifies "server-returned reason" for Stack B, but are equivalent error presentation requirements defined for Stack A React components? [Consistency, Spec §FR-030]
- [ ] CHK029 — Is the "Approve" button pattern consistent across different approval workflows — rubric approval (US3 scenario 4, draft→approved toggle), prompt approval (US3a scenario 5), and production approval (FR-054) — do they share visual hierarchy and placement conventions? [Consistency, Spec §US3, §US3a, §FR-054]
- [ ] CHK030 — Are the navigation entry points for all features (FR-023) consistently available from both the Dashboard and the Job Detail view, or are some only reachable from specific contexts? [Consistency, Spec §FR-023]

---

## Acceptance Criteria Quality

- [ ] CHK031 — Can "the interface does not time out" (US4 scenario 4) be objectively measured — is there a specific threshold for UI responsiveness during large batch uploads? [Measurability, Spec §US4 scenario 4]
- [ ] CHK032 — Can "correctly categorised candidates" (US6 independent test) be verified against specific categorisation rules — are the threshold values for longlist/shortlist/exclusion defined in the acceptance criteria? [Measurability, Spec §US6]
- [ ] CHK033 — Can "all three panes are visible and independently scrollable" (US7 scenario 1) be verified at all viewport sizes, or only desktop — what are the minimum requirements? [Measurability, Spec §US7 scenario 1]
- [ ] CHK034 — Can "stats refresh automatically within 30 seconds" (SC-003 / US8 scenario 1) be objectively tested — is this from the moment of change or from the last poll cycle? [Measurability, Spec §SC-003]
- [ ] CHK035 — Can "a recruiter can reach the ranked list view within 10 minutes of first use" (SC-006) be objectively verified — are the assumed steps, preconditions (e.g., test data already uploaded), and user skill level defined? [Measurability, Spec §SC-006]
- [ ] CHK036 — Are acceptance criteria for US3 scenario 2 (job spec upload + extraction) verifiable — how can "generate a draft rubric" be tested when the output depends on an external LLM? [Measurability, Spec §US3 scenario 2]

---

## Scenario Coverage — Workflow & Navigation

- [ ] CHK037 — Are requirements defined for back-navigation from the manual review interface — what happens to unsaved changes when the user navigates away or clicks back? [Coverage, Spec §US7]
- [ ] CHK038 — Are requirements specified for browser refresh/reload during multi-step workflows (prompt test → review → approve) — is workflow state persisted or lost? [Coverage, Gap]
- [ ] CHK039 — Are requirements defined for the transition between the results summary dialog and the manual review interface (FR-052) — does closing manual review return to the dialog, to prompt management, or to the job detail view? [Coverage, Spec §FR-052]
- [ ] CHK040 — Are requirements specified for how the recruiter discovers that a prompt test run has completed — push notification, polling with visual indicator, or manual refresh? [Coverage, Spec §FR-048]
- [ ] CHK041 — Are requirements defined for parallel/concurrent workflows — can a recruiter work on manual review for one job while another job's scoring pipeline is running? [Coverage, Gap]
- [ ] CHK042 — Are requirements specified for the job creation flow when rubric extraction fails (external API error) — can the recruiter fall back to manual rubric entry without losing already-entered form data? [Coverage, Spec §US3 scenario 2]
- [ ] CHK043 — Are requirements defined for how the recruiter transitions from the test rejection result (FR-054 / US3a scenario 15) back to prompt editing — is there a direct "Edit Prompt" action or must they navigate manually? [Coverage, Spec §FR-054]
- [ ] CHK044 — Are requirements specified for deep-linking to specific views — can a recruiter bookmark or share a URL to a specific application's manual review? [Coverage, Gap]

---

## Edge Case & Error UX Coverage

- [ ] CHK045 — Is the fallback UX defined when native document rendering fails — e.g., PDF viewer blocked by browser policy, DOCX conversion error, corrupted image file? [Edge Case, Spec §FR-058]
- [ ] CHK046 — Are requirements defined for the upload dialog UX when network connectivity is intermittent during a large batch upload — partial upload recovery, retry individual files? [Edge Case, Spec §US4 scenario 4]
- [ ] CHK047 — Is the UX defined when the user's session expires mid-workflow (e.g., during manual review or prompt editing) — is there a re-authentication flow that preserves context? [Edge Case, Gap]
- [ ] CHK048 — Are requirements defined for the UI behaviour when the external API (`AWR_SEQ_API_ENDPOINT`) is unreachable — how do prompt generation, rubric extraction, and scoring views communicate this to the user? [Edge Case, Spec §FR-042/FR-043]
- [ ] CHK049 — Is the UX specified for the "link or treat as separate" decision for duplicate applications (FR-008 / US4 scenario 3) — dialog design, bulk resolution for multiple duplicates, and undo capability? [Edge Case, Spec §US4 scenario 3]
- [ ] CHK050 — Are requirements defined for what the UI shows during the test-run status transition (pending_scoring → scoring → pending_review) — real-time status animation, polling interval, or manual refresh? [Edge Case, Spec §FR-048]
- [ ] CHK051 — Is the UX defined for the scenario where all prompt test runs are rejected — does the system provide guidance (e.g., "consider revising the rubric" or "common prompt improvement tips")? [Edge Case, Spec §US3a scenario 15]
- [ ] CHK052 — Are requirements defined for UI behaviour when rubric weights don't sum to 1.0 during job creation — is there real-time validation, auto-normalisation, or a submit-blocking error? [Edge Case, Spec §FR-028, §US3 scenario 2]

---

## Non-Functional UX Requirements

- [ ] CHK053 — Are WCAG compliance requirements specified beyond contrast ratio (≥4.5:1) — focus management, skip navigation, ARIA labels for dynamic content, screen reader announcements for status changes? [Gap, Plan §Constraints]
- [ ] CHK054 — Are keyboard navigation requirements defined for the three-pane manual review interface — tab order between panes, keyboard-driven point allocation, and focus trapping in modals? [Gap, Spec §US7]
- [ ] CHK055 — Are touch/mobile interaction requirements specified for tablet-sized viewports — are the three-pane manual review and pipeline visualiser designed for touch input? [Gap, Spec §US7, §US8]
- [ ] CHK056 — Are performance requirements defined for initial page load and view transitions — target time-to-interactive, bundle size budget, or lazy-loading strategy? [Gap]
- [ ] CHK057 — Are animation/transition requirements specified — are there motion guidelines for dialog open/close, status transitions, pane resizing, or page navigation? [Gap]
- [ ] CHK058 — Are colour/theme requirements defined beyond contrast ratio — colour palette, dark mode support, colour-blind accessible status indicators (not relying solely on colour)? [Gap]
- [ ] CHK059 — Are font/typography requirements specified — typeface, size scale, line height, and maximum line length for readability in document-heavy views? [Gap]
- [ ] CHK060 — Are responsive breakpoint requirements defined — at which viewport widths does the layout adapt, and what is the minimum supported viewport width? [Gap, Spec §US7]

---

## Dependencies & Assumptions

- [ ] CHK061 — Is the dependency on mammoth.js (or Blazor equivalent) for DOCX rendering assessed for UX quality — are rendering fidelity expectations defined, and is the user informed when conversion is lossy? [Dependency, Spec §FR-058]
- [ ] CHK062 — Is the dependency on browser-native PDF rendering assessed — are fallback requirements defined for browsers that block iframe-embedded PDFs or lack built-in PDF viewers? [Dependency, Spec §FR-058]
- [ ] CHK063 — Is the assumption that "responsive web is in scope" (Scope §Out of Scope) validated with specific responsive requirements — which views must be fully responsive and which are desktop-only? [Assumption, Spec §Scope]
- [ ] CHK064 — Is the assumption that shadcn/ui (Stack A) and Blazor component library (Stack B) provide sufficient UI primitives validated — are there custom component requirements not covered by these libraries? [Assumption, Plan §Technical Context]

---

## Ambiguities & Conflicts

- [ ] CHK065 — Does US3a scenario 14 ("no score changes in any category") conflict with FR-053 which allows the user to "accept" a test run even with score changes — is the approval gate strict (zero changes) or flexible (user-determined)? [Conflict, Spec §US3a scenario 14 vs §FR-053]
- [ ] CHK066 — Does the requirement for "immutable audit trail" in US7 scenario 5 conflict with the UX expectation of inline editing — can a recruiter freely adjust scores or does each keystroke create an audit entry? [Ambiguity, Spec §US7 scenario 5, §FR-014]
- [ ] CHK067 — Does FR-023's requirement that "no component is orphaned from the UI" conflict with the contextual availability of features — e.g., prompt management is only meaningful after job creation (US3a scenario 2)? [Conflict, Spec §FR-023 vs §US3a scenario 2]
- [ ] CHK068 — Is there a conflict between the "Approve" action in US3a scenario 5 (activates a prompt) and the "Approve for Production" action in FR-054 (production gate) — can the UI clearly distinguish these two approval types to prevent user confusion? [Ambiguity, Spec §US3a scenario 5 vs §FR-054]

---

## Notes

- Check items off as completed: `[x]`
- Add comments or findings inline
- Items are numbered sequentially (CHK001–CHK068) for easy reference
- This checklist complements `spec-quality.md` (general requirements quality), `security.md` (security requirements quality), and `parity.md` (cross-stack parity)
- Traceability: 59 of 68 items (87%) include spec/plan/data-model references
