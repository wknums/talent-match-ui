# Feature Specification: Recruiter Analytics Dashboard

**Feature Branch**: `005-recruiter-analytics`  
**Created**: 2026-03-12  
**Status**: Draft  
**Input**: User description: "Recruiter Analytics Dashboard — An analytics view accessible to admin and recruiter roles that displays performance metrics for individual recruiters and departments."

## Background

This feature already exists in Stack A (React/Express/TypeScript) but was implemented without going through the SpecKit workflow. The spec for feature 001 (core platform) explicitly listed "Advanced analytics or reporting beyond the operational dashboard" as out of scope. This specification formally defines the analytics feature to enable parity implementation in Stack B (.NET Blazor) and bring the existing Stack A implementation under SpecKit governance.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — View Recruiter Performance Summary (Priority: P1)

As an admin or recruiter, I want to see a summary of key recruitment metrics at a glance so I can quickly assess team and personal workload.

**Why this priority**: The summary stat cards are the most immediate value — they answer "How are we doing right now?" without requiring any interaction.

**Independent Test**: Can be fully tested by navigating to the analytics page and verifying that four summary stat cards display aggregated metrics. Delivers instant operational awareness.

**Acceptance Scenarios**:

1. **Given** I am logged in as an admin, **When** I navigate to the analytics view, **Then** I see four summary stat cards showing total Applications in Queue, Manual Reviews completed, Shortlist Recommendations, and Active Jobs across all recruiters.
2. **Given** I am logged in as a recruiter, **When** I navigate to the analytics view, **Then** I see summary stat cards showing metrics scoped to my department only.
3. **Given** analytics data is loading, **When** the page renders, **Then** skeleton loaders are shown for the title, stat cards, and table area.
4. **Given** there is no analytics data available, **When** the page renders, **Then** the stat cards display zero values gracefully.

---

### User Story 2 — Browse Analytics by Recruiter (Priority: P1)

As an admin, I want to view a table of individual recruiter metrics so I can identify workload distribution and individual performance across the team.

**Why this priority**: The recruiter-level table is the primary analytical view and core purpose of the feature. Combined with Story 1, it forms a complete MVP.

**Independent Test**: Can be fully tested by navigating to the analytics view, confirming the "By Recruiter" tab is active by default, and verifying that the table displays all recruiter records with the correct columns.

**Acceptance Scenarios**:

1. **Given** I am logged in as an admin and the "By Recruiter" tab is active, **When** the data loads, **Then** I see a table with columns: Recruiter, Department, Applications in Queue, Manual Reviews, Shortlist Recs, Active Jobs, and Avg. Time (hrs).
2. **Given** I am logged in as a recruiter, **When** I view the "By Recruiter" tab, **Then** I see only recruiters in my own department.
3. **Given** the recruiter table is displayed, **When** I select a department from the filter dropdown, **Then** the table filters instantly to show only recruiters in that department.
4. **Given** no recruiters match the current filter, **When** the table renders, **Then** I see an empty state message "No recruiters found".

---

### User Story 3 — Browse Analytics by Department (Priority: P2)

As an admin, I want to view aggregated metrics grouped by department so I can compare departmental performance and drill into individual recruiters within each department.

**Why this priority**: Provides a higher-level organizational view. Depends on the same data as Story 2 but presents it in a department-grouped format.

**Independent Test**: Can be fully tested by switching to the "By Department" tab and verifying that each department appears as a card with summary stats and a nested table of its recruiters.

**Acceptance Scenarios**:

1. **Given** I am logged in as an admin and I switch to the "By Department" tab, **When** the data loads, **Then** I see a card for each department showing summary stats (In Queue, Reviews, Shortlisted, Active Jobs) and a nested table of individual recruiters.
2. **Given** I am logged in as a recruiter, **When** I view the "By Department" tab, **Then** I see only my own department's card.
3. **Given** a department has no recruiters, **When** its card renders, **Then** summary stats display zero values and the nested table shows an appropriate empty state.

---

### User Story 4 — Navigate to and from Analytics (Priority: P1)

As an admin or recruiter, I want to access the analytics view from the main dashboard and easily return, so I can move between operational and analytical contexts seamlessly.

**Why this priority**: Without navigation, the analytics feature is unreachable. This is a prerequisite for all other stories.

**Independent Test**: Can be fully tested by verifying the "View Analytics" button appears for authorized roles and navigates to the analytics view, and that the "← Back to Dashboard" button returns to the dashboard.

**Acceptance Scenarios**:

1. **Given** I am logged in as an admin or recruiter, **When** I view the dashboard, **Then** I see a "View Analytics" button in the dashboard header.
2. **Given** I am logged in as a business_panel user, **When** I view the dashboard, **Then** I do NOT see the "View Analytics" button.
3. **Given** I am on the analytics view, **When** I click "← Back to Dashboard", **Then** I am returned to the main dashboard.

---

### User Story 5 — Server-Side Analytics Computation (Priority: P2)

As a system, analytics metrics must be computed from actual job and application data rather than mock/hardcoded data, so that the dashboard reflects real operational state.

**Why this priority**: Essential for production usefulness but can be deferred behind the UI stories since mock data allows UI development and testing.

**Independent Test**: Can be tested by calling the analytics API endpoints and verifying that the returned metrics match values computed from the underlying job and application data in the store.

**Acceptance Scenarios**:

1. **Given** the server has job and application data in the store, **When** the recruiter analytics endpoint is called, **Then** it returns metrics computed from actual data (not hardcoded values).
2. **Given** the server has job and application data, **When** the department analytics endpoint is called, **Then** it returns aggregated metrics grouped by department with nested recruiter records.
3. **Given** an admin calls the recruiter analytics endpoint, **When** the response is returned, **Then** it includes all recruiters across all departments.
4. **Given** a recruiter calls the recruiter analytics endpoint, **When** the response is returned, **Then** it includes only recruiters within the caller's department.

---

### User Story 6 — Server-Side Access Control (Priority: P2)

As a system, the analytics API endpoints must enforce role-based access control on the server side so that unauthorized users cannot access analytics data even if they bypass the client.

**Why this priority**: Security requirement. Currently only enforced client-side in Stack A. Server-side enforcement is required for production readiness.

**Independent Test**: Can be tested by calling analytics endpoints with different role tokens and verifying that unauthorized roles receive a 403 response.

**Acceptance Scenarios**:

1. **Given** an unauthenticated request is made to an analytics endpoint, **When** the server processes the request, **Then** it returns 401 Unauthorized.
2. **Given** a user with the business_panel role calls an analytics endpoint, **When** the server processes the request, **Then** it returns 403 Forbidden.
3. **Given** a user with admin role calls an analytics endpoint, **When** the server processes the request, **Then** it returns analytics data for all recruiters/departments.
4. **Given** a user with recruiter role calls an analytics endpoint, **When** the server processes the request, **Then** it returns analytics data scoped to the caller's department only.

---

### Edge Cases

- What happens when a recruiter has no department assigned? — They should be excluded from analytics or grouped under an "Unassigned" label.
- What happens when the data store is unavailable? — The API should return an appropriate error (500) and the UI should display an error state rather than stale data.
- What happens when a department exists but has zero recruiters? — The department should still appear in the "By Department" view with zero-value stats.
- What happens when average processing time data is not available for a recruiter? — The field should display "—" or be omitted rather than showing zero.
- What happens when a recruiter's role changes while they are viewing analytics? — The current session should continue; new requests use updated role.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide a "View Analytics" navigation button on the dashboard, visible only to users with `admin` or `recruiter` roles.
- **FR-002**: System MUST display a "← Back to Dashboard" navigation element on the analytics view that returns the user to the main dashboard.
- **FR-003**: System MUST display four summary stat cards showing aggregated totals for Applications in Queue, Manual Reviews completed, Shortlist Recommendations, and Active Jobs.
- **FR-004**: System MUST display a tabbed interface with "By Recruiter" (default) and "By Department" tabs.
- **FR-005**: The "By Recruiter" tab MUST display a table with columns: Recruiter, Department, Applications in Queue, Manual Reviews, Shortlist Recs, Active Jobs, and Avg. Time (hrs).
- **FR-006**: The "By Recruiter" tab MUST include a department filter dropdown that filters the table client-side.
- **FR-007**: The "By Department" tab MUST display a card per department with summary stats and a nested table of individual recruiters.
- **FR-008**: System MUST show skeleton loading states during data fetch for the title, stat cards, and table area.
- **FR-009**: System MUST display an empty state message "No recruiters found" when no data matches current filters.
- **FR-010**: The recruiter analytics API endpoint MUST accept authenticated requests and return recruiter-level metrics.
- **FR-011**: The department analytics API endpoint MUST accept authenticated requests and return department-level aggregated metrics with nested recruiter records.
- **FR-012**: Analytics API endpoints MUST enforce server-side RBAC: admin sees all data; recruiter sees only their department; other roles receive 403.
- **FR-013**: Analytics metrics MUST be computed from actual job and application data in the data store.
- **FR-014**: Admin users MUST see metrics across all recruiters and departments.
- **FR-015**: Recruiter users MUST see metrics scoped to their own department only.
- **FR-016**: Summary stat cards MUST reflect the same scope as the user's role (all data for admin, department-scoped for recruiter).

### Key Entities

- **RecruiterAnalytics**: Represents performance metrics for a single recruiter — includes identifier, name, department, queue count, reviews performed, shortlist recommendations, optional average processing time, and active job count.
- **DepartmentAnalytics**: Represents aggregated metrics for a department — includes department name, recruiter count, summed metrics across recruiters, active job count, and a collection of individual recruiter records.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Authorized users can navigate to the analytics dashboard and see summary stats within 2 seconds of page load.
- **SC-002**: Department filter on the "By Recruiter" tab applies instantly (no perceptible delay) on the client side.
- **SC-003**: Analytics data freshness is within 30 seconds of actual operational state.
- **SC-004**: Users with unauthorized roles (business_panel) cannot access analytics data through the UI or directly via API.
- **SC-005**: Both Stack A and Stack B implementations display identical analytics data when connected to the same data source.
- **SC-006**: All six functional user stories pass their acceptance scenarios in both stack implementations.

## Assumptions

- Recruiter and department data is available in the existing data store and can be queried to compute the required metrics.
- The existing user model includes a department field that can be used for department-based scoping.
- The existing RBAC middleware in both stacks can be extended to enforce the analytics access rules.
- Phosphor Icons (@phosphor-icons/react) is used for the icon library in Stack A; Stack B will use an equivalent icon set.
- The "By Recruiter" tab is the default active tab when the analytics view loads.

## Dependencies

- Existing authentication and RBAC middleware (both stacks)
- Data store containing job, application, and user data
- User model with department assignment and role information

## Out of Scope

- Historical trend data or time-series charts
- Export to CSV/Excel
- Custom date range filtering
- Real-time WebSocket updates
- Individual application drill-down from analytics
- Custom metric configuration or user-defined KPIs
