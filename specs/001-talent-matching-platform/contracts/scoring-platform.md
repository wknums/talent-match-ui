# Contract: Scoring Platform API

**Feature**: 001-talent-matching-platform | **Date**: 2026-03-20
**Requirements**: FR-061, FR-063, FR-067

---

## Overview

The platform API provides an asynchronous scoring submission path for high-throughput production scoring. When `AWR_PLATFORM_API_ENDPOINT` is set to a different URL than `AWR_SEQ_API_ENDPOINT`, the system routes **production scoring only** through this endpoint.

Non-scoring operations (prompt generation, extraction, test scoring) ALWAYS use `AWR_SEQ_API_ENDPOINT` regardless of scoring mode (FR-065).

## Mode Detection (FR-061, FR-066)

At startup, compare `AWR_PLATFORM_API_ENDPOINT` with `AWR_SEQ_API_ENDPOINT`:

| Condition | Mode | Behavior |
|-----------|------|----------|
| `AWR_PLATFORM_API_ENDPOINT` not set | Sequential | All scoring via `AWR_SEQ_API_ENDPOINT/assess/passthrough` |
| `AWR_PLATFORM_API_ENDPOINT` == `AWR_SEQ_API_ENDPOINT` | Sequential | Same as above |
| `AWR_PLATFORM_API_ENDPOINT` != `AWR_SEQ_API_ENDPOINT` | Platform | Production scoring via platform endpoint |

Log the resolved mode at startup for operational visibility.

## Submission Endpoint (FR-063)

```
POST {AWR_PLATFORM_API_ENDPOINT}/assess/batch
Content-Type: multipart/form-data
```

### Request FormData Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `promptFile` | File (text/plain) | Yes | Fully resolved scoring prompt with all placeholders replaced. Filename: `score-prompt.md` |
| `specFile` | File (binary) | Yes | Original candidate document (PDF, DOCX, etc.) with correct MIME type |
| `runs` | Text field | Yes | Number of scoring runs to perform (integer, e.g., `3`) |
| `callbackUrl` | Text field | No | Optional webhook URL for completion notification |

### Authentication (FR-049)

Same as sequential endpoint — headers determined by `AWR_AUTH_MODE`:

| `AWR_AUTH_MODE` | Required Headers |
|-----------------|-----------------|
| `none` | None |
| `apikey` | `X-Api-Key: <AWR_API_KEY>` |
| `entra` | `Authorization: Bearer <JWT>` |

### Response (202 Accepted)

```json
{
  "submissionId": "sub-uuid-here",
  "status": "accepted",
  "estimatedCompletionSeconds": 120,
  "pollUrl": "/assess/batch/sub-uuid-here/status"
}
```

## Polling Endpoint

```
GET {AWR_PLATFORM_API_ENDPOINT}/assess/batch/{submissionId}/status
```

### Response — In Progress

```json
{
  "submissionId": "sub-uuid-here",
  "status": "processing",
  "progress": 0.33,
  "startedAt": "2026-03-20T10:00:00Z"
}
```

### Response — Completed (200 OK)

```json
{
  "submissionId": "sub-uuid-here",
  "status": "completed",
  "result": {
    "runs": [
      {
        "runIndex": 1,
        "eligibility_gate": { "passed": true, "missing_criteria": [], "details": {} },
        "rubric_scores": [
          { "category": "Technical Skills", "score": 85.0, "evidence": "...", "section": "Experience", "confidence": 0.92 }
        ],
        "composite_score": 78.5,
        "notes": "Strong technical candidate...",
        "improvement_recommendations": ["Obtain AWS certification"]
      }
    ],
    "aggregated": {
      "final_score": 79.2,
      "variance": 3.1,
      "confidence": 0.88,
      "final_decision": "Eligible",
      "consolidated_rationale": "Consistent assessment across runs...",
      "sub_score_averages": { "Technical Skills": 83.0, "Leadership": 72.0 }
    }
  }
}
```

### Response — Failed

```json
{
  "submissionId": "sub-uuid-here",
  "status": "failed",
  "error": "LLM service unavailable",
  "failedAt": "2026-03-20T10:02:00Z"
}
```

## Polling Strategy

1. Initial poll after `estimatedCompletionSeconds / 2` (minimum 5s)
2. Subsequent polls with exponential backoff: 5s → 10s → 20s → 30s (cap)
3. Maximum polling duration: 15 minutes per submission
4. On timeout: mark application as failed, add to DLQ

## Response Format Mapping

The platform API returns the same scoring fields as the sequential API, but wrapped in a batch response structure containing both individual `runs[]` and an `aggregated` result.

The `runs[].` fields map identically to the sequential passthrough response schema (see `scoring-passthrough.md`).

The `aggregated` object maps to the `AggregatedResult` type:

| Platform Field | Application Field |
|----------------|-------------------|
| `aggregated.final_score` | `AggregatedResult.finalScore` |
| `aggregated.variance` | `AggregatedResult.variance` |
| `aggregated.confidence` | `AggregatedResult.confidence` |
| `aggregated.final_decision` | `AggregatedResult.finalDecision` |
| `aggregated.consolidated_rationale` | `AggregatedResult.rationaleText` |
| `aggregated.sub_score_averages` | `AggregatedResult.finalSubScores` |

## Consistency with Sequential Mode

Both modes produce the same downstream data:
- Individual `ScoringRun` objects stored per application
- Single `AggregatedResult` with final score, decision, and variance
- Application status transitions: Queued → Scoring → Completed/NeedsManualReview

The only difference is transport: sequential makes synchronous calls, platform uses async submission + polling.