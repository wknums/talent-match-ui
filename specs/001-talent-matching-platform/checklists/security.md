# Security Requirements Quality Checklist — 001 Talent Matching Platform

**Feature**: 001 — Talent Matching Platform
**Purpose**: Validate the completeness, clarity, consistency, and coverage of security-related requirements across spec.md, plan.md, and data-model.md
**Created**: March 20, 2026
**Depth**: Standard
**Audience**: Reviewer (PR)
**Focus Areas**: Authentication & credential management, authorization & RBAC, external API security, data protection (candidate PII), file upload security, audit trail integrity, session management

---

## Requirement Completeness

- [ ] CHK001 — Are password complexity requirements (minimum length, character classes, common-password blocklist) defined for user creation and password change? [Gap, Spec §US2, §FR-001]
- [ ] CHK002 — Are account lockout or rate-limiting requirements specified for repeated failed login attempts? [Gap, Spec §US1]
- [ ] CHK003 — Are session token properties specified — token format, entropy requirements, storage mechanism (cookie vs header), and expiration duration? [Gap, Spec §US1, §FR-021]
- [ ] CHK004 — Are CSRF protection requirements defined for state-mutating API endpoints beyond the `SameSite=Strict` cookie attribute? [Gap, Spec §FR-021]
- [ ] CHK005 — Are requirements specified for sanitizing or validating file content (not just type/size) during bulk upload to prevent malicious file injection? [Gap, Spec §US4, §FR-007]
- [ ] CHK006 — Are transport layer security requirements documented — TLS version, HSTS headers, secure-only cookies in production? [Gap]
- [ ] CHK007 — Are requirements defined for protecting candidate PII at rest — encryption of document blobs, password hashes, and personal data in storage? [Gap, Spec §FR-056]
- [ ] CHK008 — Are input validation requirements specified for all user-supplied text fields (job title, rubric categories, prompt text, comments) to prevent injection attacks? [Gap, Spec §FR-005, §FR-037]
- [ ] CHK009 — Are requirements documented for API response sanitization — ensuring internal error details, stack traces, and storage paths are not exposed to clients? [Gap]
- [ ] CHK010 — Are requirements defined for secure handling of the `AWR_API_KEY` shared secret — rotation policy, minimum entropy, and protection in environment configuration? [Gap, Spec §FR-050]
- [ ] CHK011 — Are logging requirements for security events (failed logins, privilege escalations, unauthorized access attempts) specified separately from the general audit trail? [Gap, Spec §FR-041]
- [ ] CHK012 — Are requirements defined for the `business_panel` role's access permissions, given it exists in the data model but has no corresponding authorization rules in the spec? [Gap, data-model §1 vs Spec §FR-002]
- [ ] CHK013 — Are requirements specified for secure deletion of user accounts — what happens to associated data (jobs, reviews, audit entries) when a user is deleted? [Gap, Spec §US2]

---

## Requirement Clarity

- [ ] CHK014 — Is "stored securely using one-way hashing" (FR-001) clarified with a specific algorithm recommendation? The data model specifies SHA-256, which is not recommended for password hashing by OWASP (bcrypt/scrypt/argon2 are standard). [Ambiguity, Spec §FR-001, data-model §1]
- [ ] CHK015 — Is "role-based access" (FR-002) defined with a complete permission matrix mapping each role to allowed operations on each resource type? [Clarity, Spec §FR-002]
- [ ] CHK016 — Is "no client-side API keys" (FR-017) clarified to define what constitutes a secret — does this include the `AWR_SEQ_API_ENDPOINT` URL itself, session tokens, or only authentication credentials? [Ambiguity, Spec §FR-017]
- [ ] CHK017 — Is the scope of "file type validation" for uploads quantified — is this based on file extension, MIME type header, or magic byte verification? [Clarity, Spec §FR-007, §US4 scenario 1]
- [ ] CHK018 — Is "valid file" for prompt import/upload (US3a scenario 17) defined with specific allowed formats, maximum size, and content validation criteria? [Ambiguity, Spec §US3a]
- [ ] CHK019 — Are the conditions under which `AWR_AUTH_MODE=none` is acceptable clearly constrained to development-only, with requirements preventing its use in production? [Clarity, Spec §FR-049, §Assumptions]
- [ ] CHK020 — Is "descriptive startup error" for missing auth variables (FR-050) specified with requirements for what information to include and what to redact (e.g., not logging the API key value itself)? [Clarity, Spec §FR-050]

---

## Requirement Consistency

- [ ] CHK021 — Is the password hashing algorithm consistent across all references? FR-001 says "one-way hashing", the data model and FR-020 specify "SHA-256", and the constitution mentions "SHA-256 hashing validated by PasswordHashConsistencyTests" — do all three intentionally align on SHA-256 despite it being unsuitable for password hashing? [Consistency, Spec §FR-001, §FR-020, data-model §1, Plan §Constitution]
- [ ] CHK022 — Are authorization enforcement requirements consistent between Stack A (middleware-based RBAC in `server/middleware/rbac.ts`) and Stack B (ASP.NET Core policies) — is the same permission matrix applied in both stacks? [Consistency, Plan §Constitution IV]
- [ ] CHK023 — Are file upload validation rules consistent between US3 (job spec upload: "pdf, jpg, md, txt, docx"), US4 (bulk application upload), and US3a (prompt file import) — do all upload points enforce the same security checks? [Consistency, Spec §US3, §US4, §US3a]
- [ ] CHK024 — Is the `Content-Disposition: inline` header (FR-057) for document retrieval consistent with security requirements — does serving untrusted uploaded files inline risk XSS via SVG, HTML, or polyglot files? [Conflict, Spec §FR-057 vs security best practices]
- [ ] CHK025 — Are authentication requirements for health endpoints (FR-051) consistent with the broader API security model — does explicitly unauthenticated `/healthz` and `/ready` create an information disclosure risk? [Consistency, Spec §FR-051]

---

## Acceptance Criteria Quality

- [ ] CHK026 — Can SC-007 ("Role-based access control is enforced end-to-end") be objectively tested — are specific boundary tests defined (e.g., recruiter attempts to access another department's job via direct API call)? [Measurability, Spec §SC-007]
- [ ] CHK027 — Are acceptance scenarios for US1 (authentication) sufficient to cover security edge cases — no scenarios test expired sessions, concurrent sessions, or session fixation? [Coverage, Spec §US1]
- [ ] CHK028 — Are acceptance scenarios for US2 (user management) sufficient — no scenarios test authorization boundaries (e.g., recruiter attempts admin operations via API)? [Coverage, Spec §US2]
- [ ] CHK029 — Is SC-005 ("fully traceable") measurable with specific criteria for what constitutes a complete audit record — are there minimum required fields per event type? [Measurability, Spec §SC-005]

---

## Scenario Coverage

- [ ] CHK030 — Are requirements defined for the scenario where a recruiter's role or department is changed while they have an active session — should existing sessions be invalidated? [Coverage, Gap]
- [ ] CHK031 — Are requirements defined for what happens when the JWT token for Entra ID auth (AWR_AUTH_MODE=entra) expires during a long-running scoring batch — is token refresh specified? [Coverage, Spec §FR-049]
- [ ] CHK032 — Are requirements defined for the scenario where the external scoring API (`AWR_SEQ_API_ENDPOINT`) returns a response containing malicious content (e.g., script injection in evidence citations or improvement tips)? [Coverage, Gap]
- [ ] CHK033 — Are requirements specified for handling concurrent password reset — admin resets password while user simultaneously changes their own password? [Coverage, Gap]
- [ ] CHK034 — Are requirements defined for the scenario where an attacker uploads a crafted document (e.g., PDF with embedded JavaScript, DOCX with macros) that is later rendered in the browser via the native viewer? [Coverage, Spec §FR-058]
- [ ] CHK035 — Are requirements defined for authorization checks on the document content retrieval endpoint (FR-057) — can any authenticated user access any document, or is access scoped to department/job ownership? [Coverage, Spec §FR-057]
- [ ] CHK036 — Are requirements specified for what happens when the base64-encoded document blob (FR-056) is corrupted in storage — fallback behavior for rendering and scoring? [Coverage, Spec §FR-056]

---

## Edge Case & Error Handling Coverage

- [ ] CHK037 — Is the behavior specified for when the default admin seed user (FR-020) already exists with different credentials — should the seed be skipped, updated, or cause an error? [Edge Case, Spec §FR-020]
- [ ] CHK038 — Are requirements defined for handling oversized multipart form data submissions to the scoring passthrough API — maximum payload size, timeout handling? [Edge Case, Spec §FR-045, §FR-047]
- [ ] CHK039 — Is the behavior specified when `AWR_AUTH_MODE` is set to an invalid/unrecognized value — fail-safe (deny all) or fail-open? [Edge Case, Spec §FR-050]
- [ ] CHK040 — Are requirements defined for the scenario where the audit trail storage fails — should the triggering operation also fail (transactional integrity) or proceed without audit? [Edge Case, Spec §FR-041]
- [ ] CHK041 — Is the behavior specified for API key rotation (`AWR_API_KEY`) — does the system support zero-downtime rotation, or must the service be restarted? [Edge Case, Spec §FR-050]

---

## Non-Functional Security Requirements

- [ ] CHK042 — Are security testing requirements specified — penetration testing, OWASP Top 10 coverage, or automated security scanning as part of the CI/CD pipeline? [Gap]
- [ ] CHK043 — Are data classification requirements documented — which fields constitute PII, what sensitivity level applies to candidate documents, and what handling requirements follow? [Gap]
- [ ] CHK044 — Are requirements defined for secure development practices — dependency vulnerability scanning, secret detection in source code, and security review gates? [Gap]
- [ ] CHK045 — Are Content Security Policy (CSP) headers specified to mitigate XSS risks, particularly given the native document rendering (FR-058) and iframe embedding for PDFs? [Gap, Spec §FR-058]
- [ ] CHK046 — Are requirements for cookie security attributes complete — `Secure`, `HttpOnly`, `SameSite`, `Path`, and `Max-Age`/`Expires` are specified for Stack B (FR-021) but not explicitly for Stack A? [Gap, Spec §FR-021]
- [ ] CHK047 — Are CORS requirements defined for the API — which origins are permitted, and are credentials-mode requests properly scoped? [Gap]

---

## Dependencies & Assumptions

- [ ] CHK048 — Is the assumption that SHA-256 is adequate for password hashing explicitly validated against OWASP password storage guidelines, which recommend bcrypt, scrypt, or argon2id? [Assumption, Spec §FR-001, data-model §1]
- [ ] CHK049 — Is the assumption that `AWR_AUTH_MODE=none` will not be used in production validated — are there deployment safeguards or warnings to prevent misconfiguration? [Assumption, Spec §FR-049, §Assumptions]
- [ ] CHK050 — Is the dependency on browser-embedded PDF rendering (FR-058) assessed for security — does the spec account for known PDF viewer vulnerabilities in different browsers? [Dependency, Spec §FR-058]
- [ ] CHK051 — Is the dependency on client-side DOCX-to-HTML conversion (e.g., mammoth.js) assessed for security — are sanitization requirements defined for the generated HTML to prevent XSS? [Dependency, Spec §FR-058]
- [ ] CHK052 — Are the Entra ID (Azure AD) integration requirements (FR-049) sufficient — are token validation, audience verification, and issuer validation requirements specified for the server side? [Dependency, Spec §FR-049]

---

## Ambiguities & Conflicts

- [ ] CHK053 — Does the requirement to serve documents with `Content-Disposition: inline` (FR-057) conflict with security best practices for user-uploaded content — should untrusted documents be served from a separate domain or with `Content-Disposition: attachment`? [Conflict, Spec §FR-057]
- [ ] CHK054 — Does the `business_panel` role in the data model (which has no authorization rules in the spec) create an ambiguity about what access it grants — could a user assigned this role bypass RBAC checks designed only for `admin` and `recruiter`? [Ambiguity, data-model §1 vs Spec §FR-002]
- [ ] CHK055 — Is there a conflict between "immutable audit trail" (FR-014, FR-041) and potential GDPR/data-protection right-to-erasure requirements for candidate data? [Conflict, Spec §FR-041]
- [ ] CHK056 — Does the spec adequately distinguish between authentication (verifying identity) and authorization (verifying permissions) for external API calls — FR-049 covers auth headers but are response-level authorization checks (e.g., verifying the API response hasn't been tampered with) addressed? [Ambiguity, Spec §FR-049]

---

## Notes

- Check items off as completed: `[x]`
- Add comments or findings inline
- Items are numbered sequentially (CHK001–CHK056) for easy reference
- This checklist complements `spec-quality.md` (general requirements quality) and `parity.md` (cross-stack parity)
- Traceability: 52 of 56 items (93%) include spec/plan/data-model references
