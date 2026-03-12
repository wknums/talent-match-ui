# API Contract: Get Recruiter Analytics

**Endpoint**: `GET /api/stats/recruiters`  
**Auth**: Required (auth middleware)  
**RBAC**: `admin`, `recruiter` (all other roles → 403)

## Request

No request body or query parameters.

Authentication is provided via the existing session mechanism (`AUTH_CURRENT_USER` in KV store).

## Response

### 200 OK

Returns an array of `RecruiterAnalytics` objects. The response is scoped by the caller's role:
- **Admin**: all recruiters across all departments
- **Recruiter**: only recruiters within the caller's department

```json
[
  {
    "recruiterId": "user-rec-001",
    "recruiterName": "Sarah Johnson",
    "department": "Engineering",
    "applicationsInQueue": 45,
    "manualReviewsPerformed": 23,
    "shortlistRecommendations": 18,
    "averageProcessingTime": 2.5,
    "activeJobs": 3
  },
  {
    "recruiterId": "user-rec-002",
    "recruiterName": "Michael Chen",
    "department": "Engineering",
    "applicationsInQueue": 32,
    "manualReviewsPerformed": 41,
    "shortlistRecommendations": 29,
    "activeJobs": 5
  }
]
```

**Field types**:

| Field | Type | Nullable | Notes |
|-------|------|----------|-------|
| `recruiterId` | `string` | No | User ID |
| `recruiterName` | `string` | No | Full name |
| `department` | `string` | No | Department name |
| `applicationsInQueue` | `integer` | No | ≥ 0 |
| `manualReviewsPerformed` | `integer` | No | ≥ 0 |
| `shortlistRecommendations` | `integer` | No | ≥ 0 |
| `averageProcessingTime` | `number \| null` | Yes | Hours, or `null` if not computable |
| `activeJobs` | `integer` | No | ≥ 0 |

### 401 Unauthorized

No authenticated user session.

```json
{ "error": "Unauthorized" }
```

### 403 Forbidden

Authenticated user lacks required role (`business_panel` or any non-admin/recruiter role).

```json
{ "error": "Forbidden" }
```

### 500 Internal Server Error

Storage or computation failure.

```json
{ "error": "Internal server error" }
```

## Behaviour Notes

- Recruiters without a `department` field are excluded from results (per edge case spec).
- When no recruiters exist, returns an empty array `[]`.
- `averageProcessingTime` is `null` (not `0`) when a recruiter has no completed applications.
- Results are not paginated — the recruiter count is expected to remain small (<100).
