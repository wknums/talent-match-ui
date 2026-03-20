# Contract: DELETE /api/jobs/{jobId}

## Endpoint

```
DELETE /api/jobs/{jobId}
Authorization: Required (AdminOnly policy)
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `jobId` | `string` | The GUID ID of the job to delete |

## Request Body

None.

## Responses

### 204 No Content

Job and all associated data (applications, scoring runs, config versions, documents, etc.)
successfully deleted via cascade.

### 401 Unauthorized

User is not authenticated.

### 403 Forbidden

User is authenticated but does not have the `admin` role.

### 404 Not Found

Job with the given `jobId` does not exist.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404
}
```

## Behavior Notes

- Cascade delete is handled by the database (EF Core FK configuration). All child entities
  (Applications, ScoringRuns, AggregatedResults, ApplicationDocuments, ExtractionArtifacts,
  ManualReviewData, JobConfigVersions, ScoringPrompts) are permanently removed.
- The handler also checks `ICurrentUserService.IsAdmin` as defense-in-depth (the endpoint
  policy is the primary gate).
- This is an irreversible operation. The frontend must show a confirmation dialog before calling.
- Concurrent deletion (job already deleted by another admin) returns 404.
