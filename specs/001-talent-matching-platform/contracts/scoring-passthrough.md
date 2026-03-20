# Contract: Scoring Passthrough API

**Feature**: 001-talent-matching-platform | **Date**: 2026-03-13
**Requirements**: FR-045, FR-046, FR-047

---

## Endpoint

```
POST {AWR_SEQ_API_ENDPOINT}/assess/passthrough
Content-Type: multipart/form-data
```

This is the same endpoint used by extraction (FR-043) and prompt generation (FR-034). The passthrough API is a generic reasoning engine — it accepts a system prompt and document content, forwards them to the LLM, and returns the raw response.

## Authentication (FR-049)

All requests to `/assess/passthrough` must include authentication headers determined by `AWR_AUTH_MODE`:

| `AWR_AUTH_MODE` | Required Headers | Notes |
|-----------------|-----------------|-------|
| `none` | None | Local dev — all requests accepted |
| `apikey` | `X-Api-Key: <AWR_API_KEY>` | Shared secret; optionally include `X-User-Id` and `X-User-Role` for audit |
| `entra` | `Authorization: Bearer <JWT>` | JWT obtained via MSAL / `DefaultAzureCredential` client-credentials flow |

Health endpoints (`/healthz`, `/ready`) are always unauthenticated regardless of mode.

## Request

### FormData Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `promptFile` | File (text/plain) | Yes | Fully resolved scoring prompt with all placeholders replaced. Filename: `score-prompt.md` |
| `specFile` | File (text/plain) | Yes | Candidate's extracted CV text (markdown). Filename: `candidate-cv.md` |
| `runs` | Text field | No | Number of scoring runs to execute. When provided, the engine performs N independent scoring passes and returns a combined response with individual runs and an aggregated result. Default: 1 (single run, backward-compatible). |

### Prompt Resolution

Before sending, the scoring worker resolves placeholders in the production-approved `ScoringPrompt.promptText`:

| Placeholder | Replacement | Source |
|-------------|-------------|--------|
| `{{JOB_SPEC_TEXT}}` | Job description / specification text | `Job.jobDescription` |
| `{{CANDIDATE_CV_TEXT}}` | Candidate's extracted CV markdown | `ExtractionArtifact.markdown` |

The resolved prompt becomes the `promptFile` content. The raw CV markdown is also sent as `specFile` (matching the extraction pattern where the document content is sent as `specFile`).

## Response

### Success (200 OK)

Body: Raw text — the LLM's output. Expected to be valid JSON matching the scoring output schema below.

### Error Responses

| Status | Meaning | Action |
|--------|---------|--------|
| 4xx | Client error (bad prompt, validation failure) | Log error, mark run as Failed |
| 5xx | Server error (LLM unavailable, timeout) | Retry with exponential backoff |
| Network error | Connection refused, timeout | Retry with exponential backoff |

## Scoring Output Schema (LLM JSON Response)

The scoring prompt instructs the LLM to return this JSON structure, with details and rubric scores, categories showing example values here than must be replaced with the relevant values being passed :

```json
{
  "eligibility_gate": {
    "passed": true,
    "missing_criteria": [],
    "details": {
      "5+ years Python experience": true,
      "Bachelor's degree in CS": true,
      "AWS certification": false
    }
  },
  "rubric_scores": [
    {
      "category": "Technical Skills",
      "score": 85.0,
      "evidence": "Candidate demonstrates 7 years of Python development including Django and FastAPI frameworks...",
      "section": "Professional Experience",
      "confidence": 0.92
    },
    {
      "category": "Leadership",
      "score": 70.0,
      "evidence": "Led a team of 4 developers on the payment gateway project...",
      "section": "Professional Experience",
      "confidence": 0.85
    }
  ],
  "composite_score": 78.5,
  "notes": "Strong technical candidate with solid Python and cloud experience. Leadership experience is present but limited to small teams. Missing AWS certification is a noted gap but compensated by extensive cloud deployment experience.",
  "improvement_recommendations": [
    "Obtain AWS Solutions Architect certification to strengthen cloud credentials",
    "Seek larger team leadership opportunities to demonstrate scalability"
  ]
}
```

### Field Definitions

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `eligibility_gate.passed` | boolean | Yes | Whether candidate meets all must-have criteria |
| `eligibility_gate.missing_criteria` | string[] | Yes | List of unmet must-have criteria (empty if all passed) |
| `eligibility_gate.details` | Record<string, boolean> | Yes | Per-criterion pass/fail evaluation |
| `rubric_scores` | array | Yes | Score per rubric category |
| `rubric_scores[].category` | string | Yes | Rubric category name (must match job config rubric) |
| `rubric_scores[].score` | number | Yes | Score 0–100 for this category |
| `rubric_scores[].evidence` | string | Yes | Direct quote/citation from CV supporting the score |
| `rubric_scores[].section` | string | Yes | Document section where evidence was found |
| `rubric_scores[].confidence` | number | Yes | Confidence level 0.0–1.0 for the assessment |
| `composite_score` | number | Yes | Weighted overall score 0–100 |
| `notes` | string | Yes | Summary rationale for the scoring decision |
| `improvement_recommendations` | string[] | Yes | Actionable improvement suggestions for the candidate |

## Combined Response Schema (Multi-Run with `runs` Parameter)

When `runs` > 1 is specified in the request, the response contains both individual runs and an engine-aggregated result:

```json
{
  "runs": [
    {
      "runIndex": 1,
      "eligibility_gate": { "passed": true, "missing_criteria": [], "details": {} },
      "rubric_scores": [
        { "category": "Technical Skills", "score": 85.0, "evidence": "...", "section": "Experience", "confidence": 0.92 }
      ],
      "composite_score": 78.5,
      "notes": "...",
      "improvement_recommendations": ["..."]
    },
    {
      "runIndex": 2,
      "eligibility_gate": { "passed": true, "missing_criteria": [], "details": {} },
      "rubric_scores": [
        { "category": "Technical Skills", "score": 82.0, "evidence": "...", "section": "Experience", "confidence": 0.89 }
      ],
      "composite_score": 76.0,
      "notes": "...",
      "improvement_recommendations": ["..."]
    }
  ],
  "aggregated": {
    "final_score": 77.3,
    "variance": 1.4,
    "confidence": 0.91,
    "final_decision": "Eligible",
    "consolidated_rationale": "Consistent positive assessment across multiple scoring passes...",
    "sub_score_averages": {
      "Technical Skills": 83.5,
      "Leadership": 70.0
    }
  }
}
```

### Combined Response Field Definitions

| Field | Type | Description |
|-------|------|-------------|
| `runs` | array | Individual scoring run results. Each entry follows the single-run schema above. |
| `runs[].runIndex` | number | 1-based index of this scoring run |
| `aggregated.final_score` | number | Engine-computed aggregated score across all runs |
| `aggregated.variance` | number | Score variance across runs |
| `aggregated.confidence` | number | Confidence level 0.0–1.0 based on inter-run consistency |
| `aggregated.final_decision` | string | Engine's recommended decision: "Eligible", "Excluded", or "NeedsManualReview" |
| `aggregated.consolidated_rationale` | string | Summary rationale synthesised across all runs |
| `aggregated.sub_score_averages` | Record<string, number> | Per-category score averages across all runs |

### Backward Compatibility

When `runs` is omitted or set to `1`, the response is the original single-run schema (flat JSON object, no `runs` array or `aggregated` wrapper). This preserves backward compatibility with existing callers.

## Calling Convention (Stack A — TypeScript)

```typescript
import { getAwrAuthHeaders } from '../services/awr-auth'

const formData = new FormData()
formData.append('promptFile', new Blob([resolvedPrompt], { type: 'text/plain' }), 'score-prompt.md')
formData.append('specFile', new Blob([candidateText], { type: 'text/plain' }), 'candidate-cv.md')

const authHeaders = await getAwrAuthHeaders(user) // FR-049

const response = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
  method: 'POST',
  body: formData,
  headers: { ...authHeaders },
})

const responseText = await response.text()
const parsed = JSON.parse(responseText)
```

## Calling Convention (Stack B — C#)

```csharp
// AwrAuthHandler (DelegatingHandler) automatically attaches auth headers (FR-049)
using var formData = new MultipartFormDataContent();

var promptContent = new ByteArrayContent(Encoding.UTF8.GetBytes(resolvedPrompt));
promptContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
formData.Add(promptContent, "promptFile", "score-prompt.md");

var specContent = new ByteArrayContent(Encoding.UTF8.GetBytes(candidateText));
specContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
formData.Add(specContent, "specFile", "candidate-cv.md");

// _httpClient is configured with AwrAuthHandler via IHttpClientFactory
var response = await _httpClient.PostAsync($"{endpoint}/assess/passthrough", formData, ct);
var responseText = await response.Content.ReadAsStringAsync(ct);
```

## Consistency with Existing Patterns

| Use Case | promptFile content | specFile content | Reference |
|----------|-------------------|------------------|-----------|
| Extraction (FR-043) | Extraction system prompt (hardcoded) | Uploaded document (binary) | `server/routes/jobs.ts` lines 270–290 |
| Prompt generation (FR-034) | System + user prompt (combined) | User prompt context | `server/routes/prompts.ts` lines 295–310 |
| **Scoring (FR-045)** | **Resolved scoring prompt (placeholders filled)** | **Extracted CV text (markdown)** | **This contract** |
