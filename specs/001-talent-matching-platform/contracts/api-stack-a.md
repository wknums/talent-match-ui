# API Contract — Stack A (Express / TypeScript)

**Feature**: 001-talent-matching-platform | **Date**: 2026-03-09

Base URL: `/api` | Auth: Cookie-based session | Content-Type: `application/json`

---

## Authentication

### POST /api/auth/login
Authenticate user and create session.

**Request**:
```json
{ "username": "string", "password": "string" }
```

**Response** (200):
```json
{
  "userId": "uuid", "username": "string", "role": "admin|recruiter|business_panel",
  "department": "string?", "fullName": "string", "email": "string?",
  "createdAt": "ISO8601", "lastLogin": "ISO8601?"
}
```

**Errors**: 401 Invalid credentials

### POST /api/auth/logout
**Response** (200): `{ "success": true }`

### GET /api/auth/me
**Response** (200): User object (same shape as login response)
**Errors**: 401 Not authenticated

### POST /api/auth/change-password
**Request**: `{ "currentPassword": "string", "newPassword": "string" }`
**Response** (200): `{ "success": true }`
**Errors**: 400 Current password incorrect

---

## Users (Admin Only)

### GET /api/users
**Response** (200): `User[]` (without passwordHash)

### POST /api/users
**Request**:
```json
{
  "username": "string", "role": "admin|recruiter|business_panel",
  "department": "string?", "password": "string",
  "fullName": "string?", "email": "string?"
}
```
**Response** (201): User object
**Errors**: 400 Duplicate username, validation failures

### DELETE /api/users/:userId
**Response** (200): `{ "success": true }`
**Errors**: 400 Cannot delete self

### POST /api/users/:userId/reset-password
**Request**: `{ "newPassword": "string" }`
**Response** (200): `{ "success": true }`

### GET /api/users/reset-requests
**Response** (200): `PasswordResetRequest[]` (pending only)

### POST /api/users/reset-requests
User-initiated password reset request.
**Response** (201): `PasswordResetRequest`

### PUT /api/users/reset-requests/:requestId
**Request**: `{ "action": "approve|reject", "newPassword": "string?" }`
**Response** (200): `{ "success": true }`

---

## Jobs

### GET /api/jobs
List jobs (filtered by department for recruiters).
**Response** (200): `Job[]` (with embedded stats)

### POST /api/jobs
**Request**:
```json
{
  "title": "string", "department": "string", "organization": "string",
  "postingDate": "ISO8601", "jobCode": "string", "jobDescription": "string?",
  "rubric": [{ "id": "uuid", "name": "string", "description": "string", "weight": 0.0-1.0 }],
  "mustHaves": [{ "id": "uuid", "criterion": "string", "description": "string" }],
  "desiredCriteria": [{ "id": "uuid", "qualification": "string", "description": "string" }],
  "runsPerApplication": 3, "aggregationStrategy": "median|mean|weighted",
  "longlistThreshold": 0.0-1.0, "shortlistThreshold": 0.0-1.0, "varianceThreshold": 15,
  "specDocumentId": "string?", "rubricDocumentId": "string?"
}
```
**Response** (201): `Job`
**Validation**: Rubric weights must sum to 1.0

### GET /api/jobs/:jobId
**Response** (200): `Job` (with computed stats)

### PUT /api/jobs/:jobId/config
Update job config (creates new version).
**Request**: Partial `JobConfigVersion` fields
**Response** (200): `JobConfigVersion`

### POST /api/jobs/:jobId/process
Trigger scoring pipeline for queued applications.
**Response** (200): `{ "message": "string", "queuedCount": number }`

### POST /api/jobs/extract-spec
Extract metadata from uploaded job specification document.
**Request**: `{ "fileName": "string", "content": "base64", "mimeType": "string" }`
**Response** (200): Extracted metadata JSON (title, description, requirements, rubric if present)

### POST /api/jobs/extract-rubric
Extract rubric from uploaded rubric document.
**Request**: `{ "fileName": "string", "content": "base64", "mimeType": "string" }`
**Response** (200): Extracted rubric JSON (categories with weights)

---

## Scoring Prompts *(NEW — US3a)*

### GET /api/jobs/:jobId/prompts
List all prompt revisions for a job.
**Response** (200):
```json
[{
  "promptId": "uuid", "jobId": "uuid", "versionNumber": 1,
  "promptText": "string", "status": "draft|active|inactive|production-approved",
  "createdAt": "ISO8601", "lastModifiedAt": "ISO8601", "author": "uuid",
  "rating": 0-5, "comments": "string?",
  "source": "manual|imported|generated", "generationMetadata": {}
}]
```

### POST /api/jobs/:jobId/prompts
Create a new prompt revision.
**Request**:
```json
{
  "promptText": "string",
  "source": "manual|imported|generated",
  "generationMetadata": {}
}
```
**Response** (201): `ScoringPrompt`
**Validation**: Job must exist and have an approved rubric (FR-033)

### GET /api/jobs/:jobId/prompts/:promptId
**Response** (200): `ScoringPrompt`

### PUT /api/jobs/:jobId/prompts/:promptId
Edit prompt (creates a new revision).
**Request**: `{ "promptText": "string" }`
**Response** (201): `ScoringPrompt` (new revision with incremented versionNumber)

### POST /api/jobs/:jobId/prompts/:promptId/activate
Activate this prompt revision (deactivates current active).
**Response** (200): `ScoringPrompt`
**Side Effect**: Previously active prompt set to `inactive`

### POST /api/jobs/:jobId/prompts/:promptId/rate
**Request**: `{ "rating": 0-5, "comments": "string?" }`
**Response** (200): `ScoringPrompt`

### POST /api/jobs/:jobId/prompts/generate
Generate a draft prompt from the job's approved rubric via external API.
**Response** (200): `{ "promptText": "string", "generationMetadata": {} }`
**Dependency**: Calls `AWRSEQAPI_ENDPOINT/assess/passthrough`

### POST /api/jobs/:jobId/prompts/:promptId/approve-production
Approve prompt for production scoring.
**Response** (200): `ScoringPrompt`
**Precondition**: A PromptTestRun for this prompt must have status `approved` (FR-040)

---

## Prompt Test Runs *(NEW — US3a)*

### POST /api/jobs/:jobId/prompts/:promptId/test-runs
Create a test run (upload test applications).
**Request**: `{ "files": [{ "fileName": "string", "content": "base64", "mimeType": "string", "sizeBytes": number }] }`
**Response** (201): `PromptTestRun`

### GET /api/jobs/:jobId/prompts/:promptId/test-runs
List test runs for a prompt revision.
**Response** (200): `PromptTestRun[]`

### GET /api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId
**Response** (200): `PromptTestRun` (with application details)

### POST /api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId/approve
Mark test run as approved (enables production approval for prompt).
**Response** (200): `PromptTestRun`
**Precondition**: All test applications must have completed manual review without score changes (FR-039)

---

## Applications

### POST /api/jobs/:jobId/applications/upload
Bulk upload application files.
**Request**: `{ "files": [{ "fileName": "string", "content": "base64", "mimeType": "string", "sizeBytes": number }] }`
**Response** (201): `{ "applicationIds": ["uuid"], "warnings": ["string"] }`

### GET /api/jobs/:jobId/applications
List applications with filtering and pagination.
**Query Params**: `status`, `list` (longlist|shortlist|exclusions), `sortField`, `sortOrder`, `varianceMin`, `page`, `pageSize`
**Response** (200): `{ "applications": Application[], "total": number, "page": number, "pageSize": number }`
**Note**: Excludes applications with `testRunId` from production lists

### GET /api/applications/:applicationId
Full application detail.
**Response** (200): Application with documents, extraction, scoring runs, and aggregated result

### GET /api/applications/:applicationId/runs
**Response** (200): `ScoringRun[]`

### GET /api/applications/:applicationId/result
**Response** (200): `AggregatedResult`

### GET /api/applications/:applicationId/extraction
**Response** (200): `ExtractionArtifact`

### GET /api/applications/:applicationId/manual-review
**Response** (200): `ManualReviewData | null`

### POST /api/applications/:applicationId/manual-review
Save manual review.
**Request**: `{ "rubricScores": Record, "overallComment": "string", "adjustedFinalScore": number? }`
**Response** (200): `ManualReviewData`

---

## System Operations

### GET /api/stats
**Response** (200): `SystemStats`

### GET /api/dlq
**Response** (200): `DLQItem[]`

### POST /api/dlq/:itemId/retry
**Response** (200): `{ "success": true, "message": "string" }`

### GET /api/audit
**Query Params**: `entityType`, `eventType`, `startDate`, `endDate`, `page`, `pageSize`
**Response** (200): `{ "events": ProcessingEvent[], "total": number, "page": number, "pageSize": number }`

### POST /api/llm
Server-side LLM proxy.
**Request**: `{ "prompt": "string", "model": "string?", "jsonMode": boolean? }`
**Response** (200): `{ "response": "string" }`
