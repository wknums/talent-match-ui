# Contract: Manual Review AI Prepopulation

**Feature**: 001-talent-matching-platform | **Date**: 2026-04-23  
**Requirements**: FR-014, FR-026, US7 scenarios 6-8

---

## Purpose

Define the behavior contract for prepopulating manual review rubric categories from AI scoring
results, including category score averaging, evidence display, and mismatch handling.

## Inputs

### Required Data Sources

- `ScoringRun[]` for `applicationId`.
- `AggregatedResult` for `applicationId` (for banner score/variance).
- Active job rubric categories for the target `JobConfigVersion`.
- Existing `ManualReview` data (if any).

### Evidence Citation Schema

Each scoring run citation entry must follow:

```json
{ "category": "<string>", "snippet": "<string>", "section": "<optional>", "confidence": 0.0 }
```

`category` and `snippet` are required for prepopulation.

---

## Matching Rules

For each citation or category score key, match to rubric categories using:

1. Normalized exact match
2. Normalized substring containment
3. Word-overlap fallback (minimum overlap ratio 0.4)

If no rubric category matches, the citation/score is considered unmatched.

---

## Prepopulation Rules

### Rule PR-1: First-open prepopulation

When **no existing manual review content** exists for the application and one or more matched AI
category scores exist:

- Pre-fill each rubric category score with average across all matched scoring runs.
- Pre-fill category notes/comment with:
  - `AI Score\n<score> / 100`
  - `Evidence\n- <snippet 1>\n- <snippet 2>...` (deduplicated)
- Show AI prepopulation banner with aggregated score and variance.

### Rule PR-2: Saved manual review precedence

When existing manual review content exists, AI prepopulation MUST NOT overwrite persisted
scores/comments.

### Rule PR-3: Mismatch handling

When scoring runs exist but categories/citations cannot be matched to rubric categories:

- Do not pre-fill unmatched categories.
- Surface mismatch warning (global and/or per-category) explaining that AI category labels do not
  align with current rubric.
- Allow recruiter to continue manually or re-score.

### Rule PR-4: No scoring runs

When no scoring runs exist:

- Do not show AI prepopulation banner.
- Leave category inputs/notes empty by default.

---

## Output Contract

Prepopulation result should expose at least:

```ts
interface ManualReviewPrepopulationResult {
  aiPrePopulated: boolean
  aiScoringMismatch: boolean
  rubricScores: Record<string, {
    score: number
    points: number
    maxPoints: number
    comment: string
  }>
  overallComment?: string
}
```

Optional extension for partial mismatch UX:

```ts
mismatchedCategories?: string[]
```

---

## Acceptance Mapping

- US7-6: AI score + evidence prefill on first open.
- US7-7: mismatch warning path.
- US7-8: no-scoring path (no prepopulation banner).
- FR-014: mandatory behavior for prepopulation and non-overwrite.
- FR-026: Stack B parity behavior requirement.
