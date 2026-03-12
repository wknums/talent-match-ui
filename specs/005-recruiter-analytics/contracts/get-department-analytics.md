# API Contract: Get Department Analytics

**Endpoint**: `GET /api/stats/departments`  
**Auth**: Required (auth middleware)  
**RBAC**: `admin`, `recruiter` (all other roles → 403)

## Request

No request body or query parameters.

Authentication is provided via the existing session mechanism (`AUTH_CURRENT_USER` in KV store).

## Response

### 200 OK

Returns an array of `DepartmentAnalytics` objects. The response is scoped by the caller's role:
- **Admin**: all departments
- **Recruiter**: only the caller's own department

```json
[
  {
    "department": "Engineering",
    "totalRecruiters": 3,
    "applicationsInQueue": 112,
    "manualReviewsPerformed": 87,
    "shortlistRecommendations": 64,
    "activeJobs": 11,
    "recruiters": [
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
        "averageProcessingTime": null,
        "activeJobs": 5
      }
    ]
  }
]
```

**Field types**:

| Field | Type | Nullable | Notes |
|-------|------|----------|-------|
| `department` | `string` | No | Department name (grouping key) |
| `totalRecruiters` | `integer` | No | Count of recruiters in department |
| `applicationsInQueue` | `integer` | No | Sum across department recruiters |
| `manualReviewsPerformed` | `integer` | No | Sum across department recruiters |
| `shortlistRecommendations` | `integer` | No | Sum across department recruiters |
| `activeJobs` | `integer` | No | Sum across department recruiters |
| `recruiters` | `RecruiterAnalytics[]` | No | Nested recruiter records (see recruiter contract) |

### 401 Unauthorized

```json
{ "error": "Unauthorized" }
```

### 403 Forbidden

```json
{ "error": "Forbidden" }
```

### 500 Internal Server Error

```json
{ "error": "Internal server error" }
```

## Behaviour Notes

- Departments with zero recruiters still appear in results with zero-value aggregate stats and an empty `recruiters` array.
- Aggregate fields (`applicationsInQueue`, etc.) are the sum of the corresponding fields in the nested `recruiters` array.
- `totalRecruiters` equals `recruiters.length`.
- Department names are derived from the `department` field on `User` entities, not from a separate department table.
- When no departments exist, returns an empty array `[]`.
- For a recruiter caller, returns a single-element array containing only their department.
