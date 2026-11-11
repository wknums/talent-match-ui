# Parity Checklist: 001-dynamic-rubric-editor

## Shared fixtures and contracts

- [X] Shared extraction fixture corpus exists and is versioned.
- [X] Stack A and Stack B validate `extraction-rubric.schema.json` with equivalent finding codes.
- [X] Stack A and Stack B validate `rubric-config.schema.json` with equivalent normalization outcomes.
- [X] Shared legacy conversion fixture produces equivalent `rubric-v2` proposals.

## Stack B

- [X] Shared foundations: schema, seeding, repositories, and migrations are applied idempotently.
- [X] Backend extraction uses active instruction version, stores diagnostics, and rejects invalid output.
- [X] Blazor recruiter flows create and edit `rubric-v2` payloads.
- [X] Blazor admin lifecycle supports draft, validate, activate, and rollback with admin-only mutations.
- [X] Diagnostics, legacy conversion, accessibility, and downstream scoring compatibility are validated.

## Stack A

- [X] Shared foundations read/write the same schema and active instruction state as Stack B.
- [X] Backend extraction and config persistence use the shared protected contract and `rubric-v2`.
- [X] React recruiter flows create and edit `rubric-v2` payloads.
- [X] React admin lifecycle supports draft, validate, activate, and rollback with admin-only mutations.
- [X] Diagnostics, legacy conversion, accessibility, and downstream scoring compatibility are validated.

## User stories

- [X] US1 itemized extraction parity confirmed with the representative fixture corpus.
- [X] US2 rubric item reorganization parity confirmed for pointer, keyboard, and touch flows.
- [X] US3 extraction instruction lifecycle parity confirmed for draft, validation, activation, and rollback.
- [X] US4 extraction provenance and `needs_review` diagnostics parity confirmed.

## Final validation

- [X] Targeted contract, repository, editor, and endpoint tests pass in both stacks.
- [X] Full React/Vite build and full .NET solution test pass after feature completion.
- [X] `INTEGRATION.md` and `PARITY.md` document the final endpoint and parity state.

## Current acceptance notes

- Targeted Playwright specs for Stack B onboarding and rubric editor were added and executed.
- In this environment they skipped cleanly because `E2E_STACK_B_BASE_URL`, `E2E_STACK_B_AUTH_STATE`, and/or the seeded auth fixture were not available.
- Feature-targeted Stack A validation passed: `npm test -- tests/unit/extraction-contract.test.ts tests/integration/job-spec-extraction.test.ts tests/integration/extraction-instructions.test.ts tests/unit/extraction-instruction-admin.test.tsx tests/unit/rubric-editor.test.tsx tests/unit/rubric-conversion.test.ts tests/integration/rubric-schema-migration.test.ts tests/unit/job-spec-extraction-record.test.ts tests/integration/extraction-diagnostics.test.ts tests/unit/create-job-dialog-rubric.test.ts` → 25/25 tests passed.
- Feature-targeted Stack B validation passed: Application 35/35, Infrastructure 3/3, Web 14/14, including `AuthorizationParityTests`.
- `dotnet test dotnet/TalentMatch.slnx` passed: 311/311 tests passed.
- `npm run build` passes, but Vite warns that the local Node version is `20.12.2` while Vite 7 recommends `20.19+` or `22.12+`.
- Repository-wide `npm test` is still blocked by unrelated pre-existing issues outside this feature (current result: 234 passed, 7 skipped, 3 failed, 3 worker-start errors): `tests/integration/azure-context.test.ts` fails because it expects a temp-file command log that is not created, and the existing jsdom suites (`entra-auth-ui`, `entra-access-management-ui`, `organization-admin-ui`) hit an `ERR_REQUIRE_ESM` worker-start failure in `html-encoding-sniffer`.
