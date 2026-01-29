# Talent Matching Platform

A production-grade Azure-hosted application that leverages AI reasoning models to intelligently match and score job applications against job specifications at scale, processing 20,000+ applications per job with configurable scoring runs and comprehensive audit trails.

**Experience Qualities**:
1. **Precise** - Every scoring decision is traceable with clear evidence, rationale, and actionable improvement recommendations
2. **Confident** - Real-time progress monitoring and robust failure handling provide transparency and control over large-scale processing
3. **Professional** - Enterprise-grade interface with role-based views for recruiters, hiring managers, and auditors

**Complexity Level**: Complex Application (advanced functionality, likely with multiple views)
This system orchestrates sophisticated AI-powered scoring workflows, manages massive document processing pipelines, provides multi-role dashboards with drill-down capabilities, and maintains tamper-evident audit trails—requiring coordinated state management across jobs, applications, scoring runs, and operational monitoring.

## Essential Features

**User Authentication & Role-Based Access**
- Functionality: Simple username/password authentication with admin and recruiter roles; recruiters see only their department's jobs (admins see all); password management including self-service password change and admin-managed password resets; admin user created with initial password "adm1n99"
- Purpose: Provide secure access control and personalized views for different departments while enabling password reset workflows and user management for initial demos (future: migrate to Entra ID/B2B)
- Trigger: Application load or session timeout
- Progression: Login screen → Enter credentials → Authenticate → Dashboard filtered by role/department → Access user menu for password management → Request password reset (recruiter) or manage users/reset passwords (admin) → Logout
- Success criteria: Users can only access jobs from their department (recruiters) or all jobs (admin); password changes require current password verification; admin can create users, reset passwords, and approve/reject password reset requests; admin notified of pending reset requests; all password operations secure with hashed storage

**Job Creation & Configuration**
- Functionality: Create jobs with title, department, organization, posting date, specifications, define scoring rubrics with weighted categories, configure must-have requirements, set N scoring runs per application and aggregation strategies
- Purpose: Establish versioned, auditable job configurations that drive consistent, reproducible scoring with comprehensive job metadata for tracking and filtering
- Trigger: Recruiter clicks "Create Job" from jobs dashboard
- Progression: Job details form (title, department, organization, posting date) → Upload job spec document → Define rubric categories with weights → Set must-have criteria → Configure runs (N=3 default) and aggregation (median default) → Set thresholds (longlist/shortlist) → Review and create → Job card appears with version 1.0 showing days open
- Success criteria: Job persisted with config version and metadata (organization, posting date); all settings queryable; changes create new versions without affecting in-progress scoring; job cards display organization and days since posting

**Bulk Application Ingestion**
- Functionality: Upload thousands of applications with multiple documents (CV, cover letter, portfolio), store originals with cryptographic hashes, queue for processing
- Purpose: Handle massive intake efficiently without UI timeouts or data loss
- Trigger: Recruiter selects job and clicks "Upload Applications"
- Progression: Select job → Drag-and-drop or bulk file picker → Upload progress indicator → Document validation (type, size) → Hash calculation → SQL storage → Queue creation → Confirmation with application count
- Success criteria: 20k applications uploaded within 5 minutes; each document stored with SHA-256 hash, mime type, size; all applications show "Queued" status

**Multi-Run AI Scoring Pipeline**
- Functionality: Extract documents to Markdown, execute N configurable scoring runs per application, each producing percentage score, category breakdowns, must-have evaluation, evidence citations, and improvement tips
- Purpose: Generate consistent, evidence-based assessments with variance analysis across multiple runs
- Trigger: Automatic after successful extraction; manual retry for failures
- Progression: Queued → OCR/extraction → Markdown normalization → Run 1 scoring → Run 2 scoring → Run N scoring → Store all runs with model metadata → Calculate variance → Aggregation
- Success criteria: All N runs stored individually with timestamps, token usage, model deployment ID; runs are idempotent; failures recorded with retry capability

**Aggregation & Final Assessment**
- Functionality: Combine N scoring runs using configurable strategy (median/mean/weighted), calculate confidence metrics, generate final decision (Eligible/Excluded/Manual Review), persist to immutable ledger
- Purpose: Produce defensible final scores with variance insights and consolidated rationales
- Trigger: Automatic after all N runs complete
- Progression: Fetch N runs → Apply aggregation strategy → Calculate variance/confidence → Evaluate against thresholds → Generate consolidated rationale → Merge improvement recommendations → Write to Ledger table → Update application status → Trigger list refresh
- Success criteria: Every application has immutable final result; high-variance cases flagged for manual review; exclusions always include justification + actionable feedback

**Dynamic Lists & Ranking**
- Functionality: Real-time longlist, shortlist, and exclusions with sortable columns, filters, and drill-down to individual application details
- Purpose: Enable recruiters to efficiently review candidates and understand AI decisions
- Trigger: Navigate to job detail view
- Progression: Select job → View lists tab → Apply filters (score range, variance, flags) → Click candidate → View documents, normalized Markdown, all N runs, evidence snippets, final decision → Export or advance to next stage
- Success criteria: Lists update in real-time as scoring completes; drill-down shows complete audit trail; evidence citations link to document sections

**Operational Dashboard & Monitoring**
- Functionality: System-wide and per-job statistics showing queued/processing/completed/failed counts, throughput metrics, ETA, failure browser with retry controls; filterable job view by department, organization, and job title
- Purpose: Provide operational visibility and control for large-scale processing workflows with efficient job discovery across organizational boundaries
- Trigger: Dashboard loads on login; auto-refreshes every 10 seconds
- Progression: View system stats → Apply filters (department, organization, job title) → Drill into job → See job metadata (organization, posting date, days open) → See pipeline stages (extraction, scoring, aggregation) → Identify failures → Access DLQ → Review failure reason → Retry or annotate → Monitor retry progress
- Success criteria: Stats accurate within 30 seconds; failures categorized by type; retry actions resume from checkpoint; completed work never duplicated; filters enable quick location of jobs across departments and organizations

**Tamper-Evident Audit Trail**
- Functionality: Immutable logging of all user actions (create job, upload, config changes) and system decisions (scores, aggregations) with correlation IDs
- Purpose: Ensure compliance, enable forensic analysis, provide defensibility for AI decisions
- Trigger: Every state-changing action
- Progression: Action initiated → Generate correlation ID → Capture actor, timestamp, entity details → Write to Ledger table → Link to related entities → Available for auditor queries
- Success criteria: Every final decision traceable to exact prompt version, model deployment, run artifacts; ledger cryptographically verifiable; auditor role can generate compliance reports

## Edge Case Handling

**Document Format Failures** - OCR/extraction records failure reason, marks application for manual review, provides document preview for human processing
**Model Timeout/Errors** - Exponential backoff retries with jitter; after max attempts, move to DLQ with error details and manual retry option
**High Score Variance** - Applications with variance above threshold (e.g., >15 points across runs) auto-flagged for manual review with variance visualization
**Concurrent Job Config Changes** - Versioned configs ensure in-flight scoring uses original settings; new version applies only to new applications
**Duplicate Applications** - SHA-256 hash detects duplicates; UI warns and allows linking or treating as separate
**Large Document Volumes** - Pagination with virtual scrolling; background loading of document content; progressive rendering of Markdown
**Network Interruptions** - Optimistic UI updates with rollback; background sync queue; visible sync status indicator

## Design Direction

The design should evoke **precision, confidence, and enterprise credibility**. Think mission-critical dashboards where decisions matter—similar to financial trading platforms or medical diagnostics interfaces. The aesthetic should feel intelligent and data-dense without overwhelming, using structured information hierarchy, purposeful data visualization, and calm confidence through restrained color and generous whitespace.

## Color Selection

A professional palette centered on deep navy and electric blue with warm amber accents for critical alerts and actions.

- **Primary Color**: Deep Navy `oklch(0.25 0.05 250)` - Conveys authority, trustworthiness, and enterprise-grade seriousness
- **Secondary Colors**: 
  - Slate Blue `oklch(0.45 0.08 250)` for secondary actions and muted backgrounds
  - Cool Gray `oklch(0.92 0.01 250)` for cards and surfaces
- **Accent Color**: Electric Blue `oklch(0.60 0.18 240)` - Highlights active states, progress, and primary CTAs with energy and precision
- **Alert Color**: Warm Amber `oklch(0.70 0.15 60)` - Draws attention to failures, warnings, and items needing manual review
- **Success Color**: Emerald Green `oklch(0.65 0.15 160)` - Confirms completed processing and successful scoring
- **Foreground/Background Pairings**:
  - Primary Navy on White `oklch(0.25 0.05 250)` on `oklch(0.99 0 0)` - Ratio 8.9:1 ✓
  - White on Primary Navy `oklch(0.99 0 0)` on `oklch(0.25 0.05 250)` - Ratio 8.9:1 ✓
  - Electric Blue on White `oklch(0.60 0.18 240)` on `oklch(0.99 0 0)` - Ratio 4.6:1 ✓
  - White on Electric Blue `oklch(0.99 0 0)` on `oklch(0.60 0.18 240)` - Ratio 4.6:1 ✓
  - Foreground on Cool Gray `oklch(0.20 0.02 250)` on `oklch(0.92 0.01 250)` - Ratio 11.2:1 ✓

## Font Selection

Typography should project **technical precision** and **executive clarity**—highly legible for data-dense interfaces while maintaining professional sophistication.

- **Primary Typeface**: **Space Grotesk** - A geometric sans-serif with technical character that remains approachable; excellent for UI labels and data
- **Data/Metrics Typeface**: **JetBrains Mono** - Monospace for scores, statistics, timestamps, and structured data to enhance scannability

**Typographic Hierarchy**:
- Dashboard Title: Space Grotesk Bold / 32px / -0.02em tracking
- Job/Section Headers: Space Grotesk SemiBold / 24px / -0.01em tracking
- Card Titles: Space Grotesk Medium / 18px / normal tracking
- Body Text: Space Grotesk Regular / 16px / normal tracking / 1.5 line-height
- Data/Metrics: JetBrains Mono Medium / 16px / normal tracking
- Small Scores/Labels: JetBrains Mono Regular / 14px / normal tracking
- Captions/Helper: Space Grotesk Regular / 14px / normal tracking / muted color

## Animations

Animations should reinforce **system confidence** and **data flow** without delaying actions. Use purposeful motion to indicate state transitions, guide attention to completed processing, and provide tactile feedback for critical actions. Motion feels precise and efficient—quick snaps for immediate feedback, smooth progressions for pipeline stages, and subtle pulses for live updates.

- State changes (status badges): 150ms ease-out color transitions
- Card appearances/list updates: 200ms staggered fade-up (50ms offset per item)
- Score calculations completing: 300ms spring animation with subtle scale (1.0 → 1.02 → 1.0)
- Progress bars: Smooth 500ms transitions with easing that suggests momentum
- DLQ items being retried: 200ms fade-out with slight slide
- Document drill-down: 250ms modal slide-up from bottom
- Filter application: 180ms opacity fade on list items

## Component Selection

**Components**: 
- **Dashboard Cards**: Shadcn Card with custom gradient borders for job cards; hover state reveals quick actions
- **Data Tables**: Shadcn Table with virtual scrolling for application lists; sortable columns with sticky headers
- **Progress Indicators**: Custom multi-stage progress component showing extraction → scoring → aggregation pipeline
- **Dialogs/Modals**: Shadcn Dialog for job creation, application upload flows; Sheet for application detail drill-down (slide from right)
- **Forms**: Shadcn Form with react-hook-form for job configuration; Input, Textarea, Select components
- **File Upload**: Custom drag-and-drop zone with Shadcn Card styling, progress indicators per file
- **Tabs**: Shadcn Tabs for switching between longlist/shortlist/exclusions
- **Badges**: Shadcn Badge for status indicators with custom color mapping
- **Alerts**: Sonner toasts for async operation feedback (upload complete, scoring started, failures)
- **Statistics**: Custom stat cards with big numbers in JetBrains Mono, trend indicators with Phosphor icons
- **Filters**: Shadcn Popover with checkbox groups and range sliders

**Customizations**:
- **Pipeline Visualizer**: Custom component showing 4 stages with connecting lines, dynamic status colors, completion percentages
- **Score Breakdown Chart**: Recharts radar chart for category scores across runs, showing variance visually
- **Variance Indicator**: Custom component with color-coded variance badges and sparkline of run scores
- **Evidence Viewer**: Custom component highlighting cited text passages with reference markers
- **Audit Timeline**: Custom vertical timeline component with correlation ID linking

**States**:
- Buttons: Distinct hover (scale 1.02, brightness increase), active (scale 0.98), loading (spinner + disabled), disabled (opacity 0.5)
- Inputs: Focus state with electric blue ring, error state with amber border, success checkmark for valid
- Job Cards: Default, hover (border glow), processing (animated shimmer on progress), completed (subtle success border)
- Application Rows: Default, hover (background shift), selected (electric blue left border), flagged (amber left border)

**Icon Selection**:
- Jobs: `Briefcase` for job cards
- Upload: `UploadSimple` for application ingestion
- Processing: `Gear` (animated rotation) for active scoring
- Success: `CheckCircle` for completed
- Warning: `Warning` for manual review needed
- Error: `XCircle` for failures
- Retry: `ArrowClockwise` for DLQ actions
- Filter: `Funnel` for list filtering
- Evidence: `Quotes` for citations
- Audit: `ShieldCheck` for compliance/ledger
- Statistics: `ChartBar` for metrics
- Drill-down: `ArrowRight` for navigation

**Spacing**:
- Page margins: `px-8 py-6`
- Card padding: `p-6`
- Card gaps in grid: `gap-6`
- Section spacing: `space-y-8`
- Form field gaps: `gap-4`
- Inline element gaps: `gap-2`
- Table cell padding: `px-4 py-3`

**Mobile**:
- Dashboard: Single column card stack with collapsed stats (expandable)
- Job cards: Full width with stacked actions
- Application tables: Horizontal scroll with sticky first column; consider card view toggle
- Detail drill-down: Full-screen sheet with close button top-right
- Filters: Bottom sheet instead of popover
- Pipeline visualizer: Horizontal scroll or vertical stack on small screens
