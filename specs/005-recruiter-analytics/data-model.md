# Data Model — 005-recruiter-analytics

**Feature**: Recruiter Analytics Dashboard  
**Date**: 2026-03-12

## Entities

### RecruiterAnalytics (read-only projection)

A computed view representing performance metrics for a single recruiter. Not persisted — aggregated at query time from existing Job and Application entities. All application-derived metrics MUST exclude test scoring applications (`testRunId != null`) to reflect production-only statistics (FR-038).

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `recruiterId` | `string` | ✅ | User ID of the recruiter (from `User.userId`) |
| `recruiterName` | `string` | ✅ | Display name (from `User.fullName`) |
| `department` | `string` | ✅ | Department name (from `User.department`) |
| `applicationsInQueue` | `number` | ✅ | Count of applications with status `Queued` across recruiter's jobs |
| `manualReviewsPerformed` | `number` | ✅ | Count of applications with status `NeedsManualReview` or `flagged: true` across recruiter's jobs |
| `shortlistRecommendations` | `number` | ✅ | Count of applications with `finalDecision === 'Eligible'` across recruiter's jobs |
| `averageProcessingTime` | `number \| undefined` | ❌ | Average hours from application creation to completion. `undefined` if no completed applications. |
| `activeJobs` | `number` | ✅ | Count of recruiter's jobs with status `Active` or `Processing` |

**Validation rules**:
- All numeric fields ≥ 0
- `recruiterId` must correspond to an existing user with role `recruiter` or `admin`
- `department` must be non-empty (recruiters without departments are excluded per edge case spec)

### DepartmentAnalytics (read-only projection)

A computed view representing aggregated metrics for a department, including nested recruiter detail.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `department` | `string` | ✅ | Department name (grouping key) |
| `totalRecruiters` | `number` | ✅ | Count of recruiters in this department |
| `applicationsInQueue` | `number` | ✅ | Sum of `applicationsInQueue` across all department recruiters |
| `manualReviewsPerformed` | `number` | ✅ | Sum of `manualReviewsPerformed` across all department recruiters |
| `shortlistRecommendations` | `number` | ✅ | Sum of `shortlistRecommendations` across all department recruiters |
| `activeJobs` | `number` | ✅ | Sum of `activeJobs` across all department recruiters |
| `recruiters` | `RecruiterAnalytics[]` | ✅ | Individual recruiter records within this department |

**Validation rules**:
- All numeric fields ≥ 0
- `totalRecruiters === recruiters.length`
- Aggregate fields equal the sum of corresponding fields in `recruiters` array
- Departments with zero recruiters still appear with zero-value stats and empty `recruiters` array

## Relationships

```
User (existing)
  └──< Job (existing, via job.createdBy === user.userId)
         └──< Application (existing, via jobs:{jobId}:applications KV key)

RecruiterAnalytics ── computed from ──> User + Job + Application
DepartmentAnalytics ── computed from ──> group(RecruiterAnalytics, by department)
```

## Source Data Dependencies

These existing entities are read (not modified) to compute analytics:

| Entity | Key Fields Used | KV Key Pattern |
|--------|----------------|----------------|
| `User` | `userId`, `fullName`, `role`, `department` | `users:all` |
| `Job` | `jobId`, `createdBy`, `status`, `department` | `jobs:all` |
| `Application` | `status`, `finalDecision`, `finalScore`, `flagged`, `createdAt` | `jobs:{jobId}:applications` |
| `AggregatedResult` | `createdAt` (for processing time) | `applications:{appId}:result` |

## Stack B Domain Entities

For the .NET implementation, the same logical model maps to C# records:

```csharp
// Domain/Entities/RecruiterAnalytics.cs
public record RecruiterAnalytics(
    string RecruiterId,
    string RecruiterName,
    string Department,
    int ApplicationsInQueue,
    int ManualReviewsPerformed,
    int ShortlistRecommendations,
    double? AverageProcessingTime,
    int ActiveJobs);

// Domain/Entities/DepartmentAnalytics.cs
public record DepartmentAnalytics(
    string Department,
    int TotalRecruiters,
    int ApplicationsInQueue,
    int ManualReviewsPerformed,
    int ShortlistRecommendations,
    int ActiveJobs,
    IReadOnlyList<RecruiterAnalytics> Recruiters);
```

These are value objects (immutable records) — not EF Core entities. They are projected from queries against the existing `Job`, `Application`, and `User` tables.
