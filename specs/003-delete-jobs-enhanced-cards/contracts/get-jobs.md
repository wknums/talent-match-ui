# Contract: GET /api/jobs (Enhanced Response)

## Endpoint

```
GET /api/jobs
Authorization: Required (any authenticated user)
```

## Request

No query parameters. RBAC filtering is applied server-side based on the authenticated user:
- **Admins** and users with department `"all"`: see all jobs
- **Recruiters**: see jobs they created + jobs in any of their departments

## Response

### 200 OK

Returns a JSON array of job summary objects. The response now includes three new fields compared
to the previous response shape: `createdByName`, `totalApplications`, and `completedApplications`.

```json
[
  {
    "id": "abc-123",
    "jobCode": "JOB-20260310-A1B2C3",
    "title": "Senior Developer",
    "department": "Engineering",
    "organisation": "Acme Corp",
    "postingDate": "2026-03-01T00:00:00Z",
    "status": "active",
    "currentConfigVersionId": "config-456",
    "jobDescription": "We are looking for...",
    "createdBy": "user-789",
    "createdAt": "2026-03-01T10:30:00Z",
    "createdByName": "Jane Doe",
    "totalApplications": 5,
    "completedApplications": 3
  }
]
```

### Field Descriptions

| Field | Type | Description |
|-------|------|-------------|
| `id` | `string` | Job GUID |
| `jobCode` | `string` | Auto-generated job code |
| `title` | `string` | Job title |
| `department` | `string` | Department name |
| `organisation` | `string` | Organisation name |
| `postingDate` | `DateTime` | When the job was posted |
| `status` | `string` | `"active"`, `"closed"`, `"draft"` |
| `currentConfigVersionId` | `string?` | ID of the active configuration version |
| `jobDescription` | `string?` | Job description text |
| `createdBy` | `string?` | User ID of the creator |
| `createdAt` | `DateTime` | When the job was created |
| `createdByName` | `string` | **NEW** — Full name of the creator, or `"Unknown User"` if the user record is unavailable |
| `totalApplications` | `int` | **NEW** — Total number of applications for this job |
| `completedApplications` | `int` | **NEW** — Number of applications with status `"Completed"` |

### Edge Cases

- **Deleted creator**: `createdByName` returns `"Unknown User"`, `createdBy` still contains the original user ID
- **No applications**: `totalApplications` = 0, `completedApplications` = 0
- **No jobs visible**: Returns empty array `[]`

## Breaking Change Note

The response shape is a **superset** of the previous shape — all existing fields are preserved at
the same paths. The three new fields are additive. Existing frontend code consuming the old
`JobDto` will continue to work (extra fields are ignored by `System.Text.Json` deserialization).
The frontend `JobDto` will be replaced by `JobSummaryDto` to consume the new fields.
