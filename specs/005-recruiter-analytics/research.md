# Research — 005-recruiter-analytics

**Feature**: Recruiter Analytics Dashboard  
**Date**: 2026-03-12  
**Status**: Complete

## R1: How to compute recruiter-level metrics from existing KV data

**Decision**: Aggregate at request time from existing `jobs:all` and `jobs:{jobId}:applications` KV entries. No pre-computed analytics store.

**Rationale**: The data volume is small (tens of recruiters, hundreds of applications). Real-time aggregation from the KV store avoids data staleness and requires no schema changes. The `stats.ts` route already follows this pattern — iterating over jobs and their applications at request time.

**Alternatives considered**:
- **Pre-computed cache with background refresh**: Adds complexity (background timer, invalidation). Violates YAGNI — current data volume doesn't warrant it.
- **New KV keys for analytics snapshots**: Would require write-side hooks on job/application mutations. Over-engineered for read-only analytics.

**Implementation approach**:
1. Load all jobs from `jobs:all`
2. For each job, load applications from `jobs:{jobId}:applications`
3. Load all users to get recruiter names and departments
4. Group applications by `createdBy` (recruiter userId) to compute per-recruiter metrics:
   - `applicationsInQueue`: count where status is `Queued`
   - `manualReviewsPerformed`: count where status is `NeedsManualReview` or where `flagged === true` and a manual review has been saved
   - `shortlistRecommendations`: count where `finalDecision === 'Eligible'` and `finalScore` >= job's `shortlistThreshold`
   - `activeJobs`: count of jobs with status `Active` or `Processing` created by this recruiter
   - `averageProcessingTime`: derived from application timestamps (createdAt to completion) if available; `undefined` if not computable
5. Department analytics: group recruiter records by `department` and sum metrics

## R2: Role-scoped data filtering on the server

**Decision**: Apply department filter at the route handler level based on `req.user.role` and `req.user.department`.

**Rationale**: The existing RBAC middleware (`requireRole`) handles access denial for unauthorized roles. Within authorized roles, the handler scopes data: admin gets all recruiters; recruiter gets only their department. This matches the existing pattern in `DashboardView.tsx` where recruiter filtering is done by `job.department === currentUser.department`.

**Alternatives considered**:
- **Separate endpoints per role**: Violates DRY; same data, different filters.
- **Query parameter for department**: Would require additional validation to prevent recruiters from querying other departments. Current approach is simpler and more secure.

## R3: Mapping recruiter identity to job/application ownership

**Decision**: Use `job.createdBy` field to associate jobs with recruiters. Each recruiter "owns" the jobs they created, and the applications under those jobs roll up to that recruiter's metrics.

**Rationale**: The `Job` interface already has a `createdBy: string` field (userId). This is the natural ownership model — a recruiter creates a job and manages its applications. No new fields needed.

**Alternatives considered**:
- **Explicit recruiter assignment field on jobs**: Adds complexity without benefit — `createdBy` already represents ownership.
- **Application-level recruiter field**: Applications don't have a direct recruiter field; they inherit it from their parent job. This is correct for the current model.

## R4: Stack B analytics implementation pattern

**Decision**: Follow existing Clean Architecture CQRS pattern with `GetRecruiterAnalyticsQuery` and `GetDepartmentAnalyticsQuery` in the Application layer, with data access through EF Core in Infrastructure.

**Rationale**: Stack B already has established CQRS patterns (e.g., `GetJobs`, `GetApplications` queries). Analytics queries follow the same structure. EF Core aggregation queries can leverage SQL GROUP BY for efficient computation.

**Alternatives considered**:
- **Raw SQL stored procedures**: Would break the EF Core convention established in the constitution. Only permitted for "documented performance-critical paths" — analytics at current scale doesn't qualify.
- **Materialized views**: Over-engineered for current scale. Violates YAGNI.

## R5: Frontend role-scoped stat cards

**Decision**: Move stat card aggregation to use the server-returned (already role-scoped) data rather than client-side totalling of unscoped data.

**Rationale**: Currently `AnalyticsView.tsx` computes `totalStats` by summing all recruiter data client-side. When the server returns role-scoped data (recruiter sees only their department), the client-side sum will automatically reflect the correct scope. No frontend logic change needed — just ensure the server returns pre-scoped data.

**Alternatives considered**:
- **Separate summary stats endpoint**: Unnecessary; the recruiter list endpoint already provides data that can be summed client-side. Adding a third endpoint violates YAGNI.

## R6: Average processing time computation

**Decision**: Compute `averageProcessingTime` from the difference between `application.createdAt` and the completion timestamp of the final scoring run. Return `undefined` when no completed applications exist for a recruiter.

**Rationale**: The `AggregatedResult` entity has a `createdAt` timestamp marking completion. The difference from `application.createdAt` gives processing duration. The frontend already handles `undefined` by displaying "—".

**Alternatives considered**:
- **Use `ScoringRun.durationMs`**: Only measures LLM inference time, not total processing time including queue wait. Doesn't match the user's expectation of "how long from submission to result."
- **Hardcode to zero when unavailable**: Spec explicitly says display "—" rather than zero (Edge Cases section).
