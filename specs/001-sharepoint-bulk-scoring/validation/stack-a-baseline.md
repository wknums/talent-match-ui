# Stack A baseline (T001)

## v3.1 rerun — 2026-10-13

The workstation default remains Node 20.12.2, so the exact required test selection was executed
with an isolated Node 20.19.5 runtime:

```text
npx -y -p node@20.19.5 node ./node_modules/vitest/vitest.mjs run tests/integration/application-upload-duplicates.test.ts tests/unit/upload-applications-dialog.test.tsx tests/integration/optional-upload-session.test.ts tests/integration/optional-upload-settings.test.ts tests/unit/optional-upload-settings-ui.test.tsx
```

Result: **PASS** (exit code 0).

```text
Test Files  5 passed (5)
Tests       28 passed (28)
Duration    125.63s
```

The isolated runtime reported `v20.19.5`; no application or test source was changed to obtain the
result.

## Historical 2026-10-02 run

Recorded 2026-10-02, read-only; no Stack A source was modified by this task.

Command: `npx vitest run tests/integration/application-upload-duplicates.test.ts tests/unit/upload-applications-dialog.test.tsx tests/integration/optional-upload-session.test.ts tests/integration/optional-upload-settings.test.ts tests/unit/optional-upload-settings-ui.test.tsx`

| File | Result |
| --- | --- |
| `tests/integration/application-upload-duplicates.test.ts` | passed |
| `tests/integration/optional-upload-session.test.ts` | passed |
| `tests/integration/optional-upload-settings.test.ts` | passed |
| `tests/unit/upload-applications-dialog.test.tsx` | **could not start** (environment) |
| `tests/unit/optional-upload-settings-ui.test.tsx` | **could not start** (environment) |

Totals for the files that ran: 3 files, 21 tests, all passed.

## Pre-existing environment failure

The two jsdom component suites fail before any test runs with `ERR_REQUIRE_ESM` from `html-encoding-sniffer` requiring the ESM-only `@exodus/bytes`. This workstation has **Node v20.12.2**; the repository requires **Node 20.19+** (`require(esm)` support). It is unrelated to this feature.

This historical environment limitation was resolved by the v3.1 rerun above.
