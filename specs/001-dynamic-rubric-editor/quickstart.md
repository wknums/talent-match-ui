# Quickstart: Configurable Rubric Generation and Editing

This runbook verifies the implementation described by [plan.md](plan.md) against the shared contracts.

## 1. Prerequisites

- Node.js 20+ and repository dependencies restored.
- .NET 10 SDK.
- Local shared SQLite database or an isolated Azure SQL test database.
- `AWR_SEQ_API_ENDPOINT` configured for integration extraction tests.
- An Admin account and a recruiter account.
- Representative job specifications covering compound requirements, duplicates, explicit rubrics, ambiguous categories, and invalid output fixtures.

Do not use production job specifications for prompt validation tests unless the environment's data-handling policy permits it.

## 2. Verify Contract Fixtures First

Create equivalent Stack A and Stack B tests from:

- `contracts/extraction-rubric.schema.json`
- `contracts/rubric-config.schema.json`
- Valid itemized extraction fixture.
- Compound sentence fixture that produces multiple requirement items.
- True duplicate fixture.
- Ambiguous category fixture that produces `needs_review`.
- Invalid JSON, missing-field, invalid-weight, and missing-source fixtures.

Expected:

- Both validators accept and normalize the same valid fixtures.
- Both validators reject the same invalid fixtures with equivalent finding codes.
- No invalid extraction is returned as a successful job-form payload.

## 3. Verify Shared Schema and Seed

Start each stack once against a fresh local database, then against a database containing legacy job configs.

Expected:

1. `ExtractionInstructionVersions` and `JobSpecExtractions` exist with equivalent constraints.
2. Exactly one version 1 instruction is seeded only when the version table is empty.
3. Restarting either stack does not create another seed.
4. Both stacks read the same active instruction.
5. Existing legacy rubric JSON remains unchanged and readable.

Run targeted schema/repository tests before application tests:

```powershell
npm test -- tests/integration/rubric-schema-migration.test.ts tests/integration/extraction-instructions.test.ts
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~ExtractionInstruction|FullyQualifiedName~JobSpecExtraction|FullyQualifiedName~JobConfigVersion"
```

## 4. Verify Administrator Instruction Lifecycle

As Admin:

1. Open extraction-instruction administration.
2. Confirm the active version and protected contract are visible.
3. Create a draft that strengthens individual-requirement itemization.
4. Validate it with a sample specification.
5. Inspect itemized output and validation findings.
6. Activate the valid draft.
7. Generate a new job extraction and confirm its version ID.
8. Reactivate the prior valid version as rollback.

Expected:

- Saving a draft does not change the active version.
- Invalid output blocks activation.
- Activation is atomic and leaves one active version.
- Rollback affects only subsequent extractions.
- Every state change emits a correlated audit event without full prompt or document content.

As recruiter, call every administration mutation endpoint and verify `403`.

## 5. Verify Extraction Completeness

Upload a specification containing:

- Multiple requirements in one bullet.
- Repeated mandatory and preferred wording.
- Years-of-experience and certification requirements.
- Responsibilities and soft-skill requirements.
- One requirement with ambiguous category placement.

Expected:

1. Every independently assessable requirement appears once as an item or is explicitly flagged.
2. Compound wording is split without changing meaning.
3. Only true duplicates are consolidated.
4. Every extracted item includes source text.
5. Ambiguous items remain present with `needs_review`.
6. Category weights sum to exactly 1.0.
7. The extraction record retains raw/normalized output, findings, actor, correlation, and instruction/contract versions.

## 6. Verify Rubric Editing

In each client:

1. Open a generated `rubric-v2`.
2. Drag an item within its category.
3. Drag an item to an empty category.
4. Use keyboard controls to move another item.
5. Use explicit destination/position controls in a touch viewport.
6. Edit, add, and remove an individual item.
7. Save and reopen.

Expected:

- Drop target and destination position are visible.
- Assistive technology receives a move announcement.
- Item ID, text, type, and source trace do not change during movement.
- Category weights do not change.
- Orders are normalized and persist after reload.
- A simulated save failure retains unsaved local state.
- A stale config version returns conflict and never overwrites the newer version.

Run component/accessibility tests:

```powershell
npm test -- tests/unit/rubric-editor.test.tsx
dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~RubricEditor"
npx playwright test --grep "rubric editor"
```

## 7. Verify Legacy Conversion

Open a legacy rubric containing several requirements in one description.

Expected:

1. The legacy rubric remains readable.
2. Item drag/drop is unavailable until conversion.
3. Conversion first displays a reviewable proposal.
4. Cancelling leaves all data unchanged.
5. Confirming creates a new config version with `rubric-v2`.
6. The original legacy config remains unchanged.
7. Existing application detail, prompt generation, scoring category remapping, and manual review continue to display category names and weights correctly.

## 8. Regression and Parity Validation

Run the smallest relevant suites first, then full validation:

```powershell
npm test
npm run build
dotnet test dotnet/TalentMatch.slnx
```

Verify both stacks against the same database and contract fixtures. Update `INTEGRATION.md` endpoint mappings before completion.

## 9. Acceptance Exit Criteria

- All explicitly stated requirements in the representative set are individual items or flagged.
- No invalid instruction version can activate.
- One active version exists after activation and rollback.
- Pointer, keyboard, and touch-equivalent moves persist.
- Legacy configs remain readable and convert only with confirmation.
- Existing scoring behavior remains category-based and passes regression tests.
- Stack A and Stack B produce equivalent API responses, validation findings, and persisted rubric envelopes.

## 10. Current Validation Snapshot

- Stack B targeted validation completed:
  - `dotnet test dotnet/tests/Application.Tests/TalentMatch.Application.Tests.csproj --filter "FullyQualifiedName~JobSpecExtraction|FullyQualifiedName~GeneratePromptCommandTests|FullyQualifiedName~ScoreApplicationCommandEvidenceParsingTests|FullyQualifiedName~ExtractionInstructionCommandTests|FullyQualifiedName~UpdateJobConfigRubricV2Tests"`
  - `dotnet test dotnet/tests/Infrastructure.Tests/TalentMatch.Infrastructure.Tests.csproj --filter "FullyQualifiedName~JobSpecExtractionRepositoryTests|FullyQualifiedName~JobConfigVersionMappingTests"`
  - `dotnet test dotnet/tests/Web.Tests/TalentMatch.Web.Tests.csproj --filter "FullyQualifiedName~JobSpecExtractionEndpointsTests|FullyQualifiedName~LegacyRubricConversionTests|FullyQualifiedName~RubricEditorTests|FullyQualifiedName~ExtractionInstructionEndpointsTests|FullyQualifiedName~ExtractionInstructionAdminTests|FullyQualifiedName~JobSpecExtractionDiagnosticsTests|FullyQualifiedName~AuthorizationParityTests"`
- Stack A targeted validation completed:
  - `npm test -- tests/unit/extraction-contract.test.ts tests/integration/job-spec-extraction.test.ts tests/integration/extraction-instructions.test.ts tests/unit/extraction-instruction-admin.test.tsx tests/unit/rubric-editor.test.tsx tests/unit/rubric-conversion.test.ts tests/integration/rubric-schema-migration.test.ts tests/unit/job-spec-extraction-record.test.ts tests/integration/extraction-diagnostics.test.ts tests/unit/create-job-dialog-rubric.test.ts`
  - `npm run build:server`
  - `npm run build`
- Full .NET validation completed: `dotnet test dotnet/TalentMatch.slnx` → 311/311 tests passed.
- Repository-wide `npm test` was executed but is currently blocked by unrelated pre-existing failures outside this feature (current result: 234 passed, 7 skipped, 3 failed, 3 worker-start errors):
  - `tests/integration/azure-context.test.ts` expects a temp-file command log that is not created in this environment.
  - Existing jsdom suites (`tests/unit/entra-auth-ui.test.tsx`, `tests/unit/entra-access-management-ui.test.tsx`, `tests/unit/organization-admin-ui.test.tsx`) fail during worker startup with `ERR_REQUIRE_ESM` in `html-encoding-sniffer`.
- Playwright feature specs were added for Stack B onboarding and rubric editing. `npx playwright test tests/e2e/stack-b-job-onboarding.spec.ts tests/e2e/stack-b-rubric-editor.spec.ts` currently skips all 10 cases when environment-backed prerequisites (`E2E_STACK_B_BASE_URL`, `E2E_STACK_B_AUTH_STATE`, `E2E_JOB_SPEC_FILE`) are unavailable.
