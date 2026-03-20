# Spec Quality Checklist — 001 Talent Matching Platform

**Feature**: 001 — Talent Matching Platform
**Purpose**: Validate the completeness, clarity, consistency, and coverage of requirements in spec.md, plan.md, and data-model.md
**Created**: March 12, 2026
**Depth**: Standard
**Audience**: Reviewer (PR)
**Focus Areas**: Full-spectrum requirements quality — auth & security, AI/LLM integration, data pipeline, UI/UX flows, edge cases, non-functional requirements

---

## Requirement Completeness

- [ ] CHK001 — Are file size limits specified for each accepted document type (pdf, jpg, md, txt, docx) in the upload requirements? [Gap, Spec §US4]
- [ ] CHK002 — Are the specific retry counts and backoff intervals defined for AI model timeout/error scenarios? [Gap, Spec §FR-013]
- [ ] CHK003 — Are the default values for longlist/shortlist thresholds documented in the requirements? [Gap, Spec §FR-005]
- [ ] CHK004 — Are requirements defined for what happens when an admin deletes a user who owns active jobs or in-progress scoring runs? [Gap, Spec §US2]
- [ ] CHK005 — Is the maximum number of concurrent scoring runs per job or system-wide specified? [Gap, Spec §FR-009]
- [ ] CHK006 — Are requirements for session expiration and timeout behavior documented? [Gap, Spec §US1]
- [ ] CHK007 — Is the draft rubric approval workflow fully specified — what UI controls exist for toggling draft-to-approved, and is re-editing an approved rubric addressed? [Completeness, Spec §US3 scenario 2]
- [ ] CHK008 — Are requirements defined for the "link or treat as separate" decision when duplicate applications are detected — what does "link" mean in this context? [Gap, Spec §FR-008, US4 scenario 3]
- [ ] CHK009 — Are loading/progress indicator requirements defined for the AI scoring pipeline (US5), not just document extraction (FR-044)? [Gap, Spec §US5]
- [ ] CHK010 — Are requirements specified for partial batch upload failure — what happens if 3 of 50 files fail validation during bulk upload? [Gap, Spec §US4]
- [ ] CHK011 — Is the behavior specified for when `AWR_SEQ_API_ENDPOINT` is not configured or unreachable at application startup? [Gap, Spec §FR-042/FR-043]
- [ ] CHK012 — Are requirements for the "pipeline visualiser" (US8 scenario 4) defined beyond the stage names — layout, interaction model, real-time update mechanism? [Gap, Spec §US8]
- [ ] CHK013 — Are requirements documented for what happens to in-progress prompt test runs when the prompt is edited or deactivated mid-test? [Gap, Spec §US3a]
- [ ] CHK014 — Are mobile/responsive layout requirements defined for the three-pane manual review interface (US7)? [Gap, Spec §US7]

---

## Requirement Clarity

- [ ] CHK015 — Is "approved scoring rubric" precisely defined — does it mean the draft-to-approved toggle (US3 scenario 2) or a separate approval action? [Ambiguity, Spec §FR-033, US3a scenario 3]
- [ ] CHK016 — Is the term "configurable strategy" for aggregation (median/mean/weighted) clarified — who configures it, when, and what are the defaults? [Ambiguity, Spec §FR-011]
- [ ] CHK017 — Is "variance above the configured threshold" quantified with a default value (the edge case mentions ">15 points" but FR-012 says "configured threshold")? [Ambiguity, Spec §FR-012 vs Edge Cases]
- [ ] CHK018 — Is "within 15 minutes of upload" (SC-002) defined as wall-clock time from upload completion or from when the item enters the scoring queue? [Ambiguity, Spec §SC-002]
- [ ] CHK019 — Is the difference between "Approve" (US3a scenario 5, activates prompt) and "Approve for Production" (US3a scenario 14, after test review) clearly distinguished in the requirements? [Ambiguity, Spec §US3a]
- [ ] CHK020 — Is "zero data loss" (SC-001) defined with specific failure recovery guarantees (e.g., transactional upload, idempotent retries)? [Ambiguity, Spec §SC-001]
- [ ] CHK021 — Are the terms "Eligible", "Excluded", and "Needs Manual Review" decisions defined with specific criteria or score thresholds that determine each? [Clarity, Spec §FR-011, US5 scenario 3]
- [ ] CHK022 — Is "balanced visual weight" or "visual hierarchy" used anywhere in the spec, and if so, is it quantified with measurable properties? [Clarity]
- [ ] CHK023 — Is "small set of real example applications" for prompt testing (US3a) quantified — any minimum or maximum? [Ambiguity, Spec §US3a]
- [ ] CHK024 — Is "resource usage metrics" in FR-010 defined — CPU time, token count, API cost, latency, or all of these? [Ambiguity, Spec §FR-010]

---

## Requirement Consistency

- [ ] CHK025 — Are the password hashing requirements consistent — the spec says "SHA-256" (FR-001, data model) while the constitution mentions "SHA-256 hashing" — is this intentionally not bcrypt/argon2, and is this consistent with security best practices? [Consistency, Spec §FR-001, Plan §Constitution]
- [ ] CHK026 — Is the `AWR_SEQ_API_ENDPOINT` environment variable name consistent across the spec? US3 uses `AWR_SEQ_API_ENDPOINT` (with underscores) while US3a and FR-043 use `AWR_SEQ_API_ENDPOINT` (no underscores). [Conflict, Spec §US3 vs §FR-043]
- [ ] CHK027 — Do US3a scenario 5 ("Approve" activates a prompt) and FR-036 ("only one active prompt") align with US3a scenario 7 ("Activate" as a separate action)? Are "Approve" and "Activate" the same or different actions? [Conflict, Spec §US3a scenarios 5 vs 7]
- [ ] CHK028 — Is the user role model consistent between spec and data model? The data model includes `business_panel` role but the spec only mentions `admin` and `recruiter`. [Conflict, Spec §FR-002 vs data-model §1]
- [ ] CHK029 — Are supported document types consistent — US3 lists "pdf, jpg, md, txt, docx" but US4 and assumptions mention "PDF, DOCX, MD" with different case and scope? [Consistency, Spec §US3 vs §US4 vs §Assumptions]
- [ ] CHK030 — Is the "days since posting" display (US3 scenario 6) consistent with the job card requirements in FR-005 and US8 — are all views of job cards expected to show this? [Consistency, Spec §US3 scenario 6]

---

## Acceptance Criteria Quality

- [ ] CHK031 — Can SC-001 ("20,000 applications within 90 minutes") be objectively measured — are test environment specs, network conditions, and file sizes defined? [Measurability, Spec §SC-001]
- [ ] CHK032 — Can SC-006 ("within 10 minutes of first use") be objectively verified — does this assume prior knowledge, a guided flow, or documentation? [Measurability, Spec §SC-006]
- [ ] CHK033 — Is SC-004 ("100% accuracy" for variance flagging) a meaningful criterion given it's a deterministic comparison, or should it specify edge cases like NaN/infinity handling? [Measurability, Spec §SC-004]
- [ ] CHK034 — Are acceptance scenarios for US4 (bulk upload) sufficient — only 4 scenarios for a complex upload pipeline with validation, fingerprinting, progress tracking, and error handling? [Coverage, Spec §US4]
- [ ] CHK035 — Are acceptance scenarios for US8 (pipeline monitoring) sufficient — only 4 scenarios for dashboard stats, filtering, failure queue, and pipeline visualiser? [Coverage, Spec §US8]
- [ ] CHK036 — Does US7 have acceptance criteria for the resizable pane behavior — minimum widths, collapse behavior, responsive breakpoints? [Gap, Spec §US7]

---

## Scenario Coverage

- [ ] CHK037 — Are requirements defined for concurrent access scenarios — what happens when two recruiters edit the same job configuration simultaneously? [Coverage, Gap]
- [ ] CHK038 — Are requirements defined for the scenario where the external API (`AWR_SEQ_API_ENDPOINT`) returns malformed or unexpected JSON during extraction? [Coverage, Spec §FR-043/FR-044]
- [ ] CHK039 — Are requirements specified for what happens when a job is deleted or archived while applications are mid-pipeline (Extracting/Scoring/Aggregating)? [Coverage, Gap]
- [ ] CHK040 — Is the recovery flow defined for when a prompt test run is interrupted (e.g., server restart) — should test-case applications be cleaned up or resumed? [Recovery, Gap]
- [ ] CHK041 — Are requirements defined for the scenario where rubric weights don't sum to 1.0 due to floating-point precision issues? [Edge Case, Spec §US3 scenario 2]
- [ ] CHK042 — Is the behavior specified for scoring an application when the active prompt is changed between scoring runs N-1 and N? [Edge Case, Spec §FR-009/FR-035]
- [ ] CHK043 — Are requirements defined for empty-state scenarios — a job with zero applications, a dashboard with zero jobs, a prompt list with zero revisions? [Coverage, Gap]
- [ ] CHK044 — Are requirements specified for what "manual retry" means for extraction failures (US5 scenario 5) — UI controls, who can retry, retry limits? [Gap, Spec §US5]

---

## Edge Case & Error Handling Coverage

- [ ] CHK045 — Is the fallback behavior defined when the auto-generated draft rubric's weight distribution (60% to must-haves) has zero must-have criteria? [Edge Case, Spec §US3 scenario 2]
- [ ] CHK046 — Are requirements defined for prompt file import when the file contains invalid or extremely large content? [Edge Case, Spec §US3a scenario 17]
- [ ] CHK047 — Is the behavior specified when a recruiter tries to "Approve for Production" a prompt that has been deactivated since the test run completed? [Edge Case, Spec §US3a]
- [ ] CHK048 — Are error handling requirements defined for the audit ledger — what happens if audit event persistence fails during a state-changing operation? [Edge Case, Spec §FR-041]
- [ ] CHK049 — Is the behavior specified for duplicate fingerprint detection across different jobs — or is fingerprinting scoped per-job only? [Edge Case, Spec §FR-008]
- [ ] CHK050 — Are requirements defined for what happens when the configured number of scoring runs (N) is changed on a job configuration version that already has partially completed runs? [Edge Case, Spec §FR-006/FR-009]

---

## Non-Functional Requirements Coverage

- [ ] CHK051 — Are accessibility requirements (WCAG compliance level, keyboard navigation, screen reader support) specified beyond the ≥4.5:1 contrast ratio? [Gap, Plan §Constraints]
- [ ] CHK052 — Are data retention and cleanup requirements defined — how long are completed scoring runs, test cases, and audit logs retained? [Gap]
- [ ] CHK053 — Are browser compatibility requirements specified — which browsers and minimum versions are supported? [Gap]
- [ ] CHK054 — Are internationalization/localization requirements explicitly scoped out or deferred — the assumptions mention English-only but no FR addresses this? [Gap, Spec §Assumptions]
- [ ] CHK055 — Are performance requirements defined for the manual review three-pane interface with large documents (e.g., 50+ page CVs)? [Gap, Spec §US7]
- [ ] CHK056 — Are logging and observability requirements defined beyond the audit ledger — structured logging, correlation IDs across API boundaries, error monitoring? [Gap]

---

## Dependencies & Assumptions

- [ ] CHK057 — Is the assumption that SHA-256 is sufficient for password hashing validated against current security standards (OWASP recommends bcrypt/scrypt/argon2)? [Assumption, Spec §FR-001]
- [ ] CHK058 — Is the external API availability assumption documented with SLA expectations, and are degraded-mode requirements defined when `AWR_SEQ_API_ENDPOINT` is unavailable? [Assumption, Spec §Assumptions]
- [ ] CHK059 — Is the single-tenant assumption explicitly documented as a constraint with migration path considerations for future multi-tenancy? [Assumption, Spec §Assumptions]
- [ ] CHK060 — Are the EF Core migration requirements for Stack B defined — who runs migrations, when, and what happens on schema conflicts? [Dependency, Plan §Technical Context]

---

## Traceability & Specification Gaps

- [ ] CHK061 — Is a requirement ID scheme established that maps FR numbers to acceptance scenarios — several FRs (FR-032 through FR-044) were added for US3a but not all acceptance scenarios reference FR IDs? [Traceability]
- [ ] CHK062 — Are the "Edge Cases" section items (document format failures, AI model timeout, high score variance, etc.) traced to specific FRs or acceptance scenarios? [Traceability, Spec §Edge Cases]
- [ ] CHK063 — Is there a gap in requirements for the `business_panel` role mentioned in the data model but absent from all user stories and FRs? [Gap, data-model §1 vs Spec]
- [ ] CHK064 — Are requirements for the SignalR real-time updates (mentioned in Plan §Constitution Check for Stack B) documented in the spec, or is this an implementation detail without requirements backing? [Traceability, Gap]
- [ ] CHK065 — Does the spec define what "optimistic updates with rollback" (Edge Cases §Network interruptions) means in concrete terms — which operations, what rollback mechanism, what user feedback? [Gap, Spec §Edge Cases]
