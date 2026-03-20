# Data Model — 003-delete-jobs-enhanced-cards

## Existing Entities (no changes)

No database migrations are needed. All required data already exists in the schema.

### Job (unchanged)

| Field | Type | Notes |
|-------|------|-------|
| Id | `string` | GUID, primary key |
| JobCode | `string` | Auto-generated (JOB-date-suffix) |
| Title | `string` | Display name |
| Department | `string` | Department filter |
| Organisation | `string` | Organisation name |
| PostingDate | `DateTime` | When the job was posted |
| Status | `string` | `"active"`, `"closed"`, `"draft"` |
| CreatedBy | `string?` | FK to User.Id — used to resolve creator name |
| JobDescription | `string?` | Job description text |
| CurrentConfigVersionId | `string?` | FK to active config version |
| CreatedAt | `DateTime` | Creation timestamp — displayed on enhanced cards |
| UpdatedAt | `DateTime` | Last update timestamp |

**Cascade delete relationships** (configured in EF Core migrations):
- Job → Application (cascade)
- Application → ScoringRun (cascade)
- Application → AggregatedResult (cascade)
- Application → ApplicationDocument (cascade)
- Application → ExtractionArtifact (cascade)
- Application → ManualReviewData (cascade)
- Job → JobConfigVersion (cascade) — includes rubric data (RubricJson), must-have criteria, desired criteria, and scoring configuration. The rubric's implicit approval status (determined by `RubricJson != null`) is removed along with the config version.

### Application (unchanged — used for counts)

| Field | Type | Notes |
|-------|------|-------|
| Id | `string` | GUID, primary key |
| JobId | `string` | FK to Job.Id |
| Status | `string` | `"Queued"`, `"Extracting"`, `"Scoring"`, `"Aggregating"`, `"Completed"`, `"Failed"`, `"NeedsManualReview"` |

### User (unchanged — used for name lookup)

| Field | Type | Notes |
|-------|------|-------|
| Id | `string` | GUID, primary key |
| FullName | `string` | Displayed as creator name on job cards |

---

## New DTOs / Records

### JobSummaryDto (Application layer)

Returned by `GetJobSummariesQuery`. Contains all existing job fields plus joined/aggregated data.

```csharp
public record JobSummaryDto(
    string Id,
    string JobCode,
    string Title,
    string Department,
    string Organisation,
    DateTime PostingDate,
    string Status,
    string? CurrentConfigVersionId,
    string? JobDescription,
    string? CreatedBy,
    DateTime CreatedAt,
    string CreatedByName,        // NEW: User.FullName or "Unknown User"
    int TotalApplications,       // NEW: count of all applications for this job
    int CompletedApplications    // NEW: count of applications with Status == "Completed"
);
```

### DeleteJobCommand (Application layer)

```csharp
public record DeleteJobCommand(string JobId) : IRequest<bool>;
```

### JobSummaryDto (Frontend — ApiClient.cs)

```csharp
public record JobSummaryDto(
    string Id,
    string JobCode,
    string Title,
    string Department,
    string Organisation,
    DateTime PostingDate,
    string Status,
    string? CurrentConfigVersionId,
    string? JobDescription,
    string? CreatedBy,
    DateTime CreatedAt,
    string CreatedByName,
    int TotalApplications,
    int CompletedApplications
);
```

---

## State Transitions

None — deletion is a terminal action, not a state change. The job and all associated data are
permanently removed. No soft-delete or status change involved.

---

## Validation Rules

| Context | Rule | Enforcement |
|---------|------|-------------|
| DeleteJobCommand | Current user must be admin | Handler checks `ICurrentUserService.IsAdmin`; endpoint uses `AdminOnly` policy |
| DeleteJobCommand | Job must exist | Handler returns `false` if `GetByIdAsync` returns null; endpoint returns 404 |
| Confirmation dialog | Job title displayed | Frontend reads from existing job data |
| Cascade warning | Application count > 0 triggers warning | Frontend reads `TotalApplications` from `JobSummaryDto` |
