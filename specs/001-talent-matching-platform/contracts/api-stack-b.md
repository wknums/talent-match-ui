# API Contract — Stack B (ASP.NET Core Minimal APIs)

**Feature**: 001-talent-matching-platform | **Date**: 2026-03-09

Base URL: `/api` | Auth: Cookie-based (ASP.NET Core Authentication) | Content-Type: `application/json`

The Stack B API mirrors the Stack A contract to maintain feature parity. Both stacks implement the same endpoints with the same request/response shapes. This document notes Stack B-specific implementation details.

---

## Architecture Notes

- **CQRS**: All operations dispatched via MediatR (`IMediator.Send()`)
- **Validation**: FluentValidation via `ValidationBehaviour<TRequest, TResponse>` pipeline
- **Persistence**: EF Core repositories (SQLite dev / Azure SQL prod)
- **Auth**: Cookie auth with `[Authorize]` / role-based policies
- **Endpoint Registration**: Minimal APIs in `Web.Server/Endpoints/`

---

## Authentication — `AuthEndpoints.cs`

| Method | Path | MediatR Handler | Notes |
|--------|------|------------------|-------|
| POST | `/api/auth/login` | Direct (SHA-256 hash comparison) | Sets auth cookie via `HttpContext.SignInAsync` |
| POST | `/api/auth/logout` | Direct | `HttpContext.SignOutAsync` |
| GET | `/api/auth/me` | `GetCurrentUserQuery` | Uses `ICurrentUserService` |
| POST | `/api/auth/change-password` | `ChangePasswordCommand` | Validates current password first |

**Cookie Config**: `HttpOnly=true`, `SameSite=Strict`, `SecurePolicy=SameAsRequest` (FR-021)

---

## Users — `UserEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| GET | `/api/users` | `GetUsersQuery` | Admin |
| POST | `/api/users` | `CreateUserCommand` | Admin |
| DELETE | `/api/users/{userId}` | `DeleteUserCommand` | Admin |
| POST | `/api/users/{userId}/reset-password` | `ResetPasswordCommand` | Admin |
| GET | `/api/users/reset-requests` | `GetResetRequestsQuery` | Admin |
| POST | `/api/users/reset-requests` | `RequestPasswordResetCommand` | Authenticated |
| PUT | `/api/users/reset-requests/{requestId}` | `ResolveResetRequestCommand` | Admin |

---

## Jobs — `JobEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| GET | `/api/jobs` | `GetJobsQuery` | Authenticated |
| POST | `/api/jobs` | `CreateJobCommand` | Authenticated |
| GET | `/api/jobs/{jobId}` | `GetJobDetailQuery` | Authenticated |
| PUT | `/api/jobs/{jobId}/config` | `UpdateJobConfigCommand` | Authenticated |
| POST | `/api/jobs/{jobId}/process` | `ProcessJobCommand` | Authenticated |
| POST | `/api/jobs/extract-spec` | `ExtractSpecCommand` | Authenticated |
| POST | `/api/jobs/extract-rubric` | `ExtractRubricCommand` | Authenticated |

---

## Scoring Prompts *(NEW — US3a)* — `PromptEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| GET | `/api/jobs/{jobId}/prompts` | `GetPromptsQuery` | Authenticated |
| POST | `/api/jobs/{jobId}/prompts` | `CreatePromptCommand` | Authenticated |
| GET | `/api/jobs/{jobId}/prompts/{promptId}` | `GetPromptQuery` | Authenticated |
| PUT | `/api/jobs/{jobId}/prompts/{promptId}` | `EditPromptCommand` | Authenticated |
| POST | `/api/jobs/{jobId}/prompts/{promptId}/activate` | `ActivatePromptCommand` | Authenticated |
| POST | `/api/jobs/{jobId}/prompts/{promptId}/rate` | `RatePromptCommand` | Authenticated |
| POST | `/api/jobs/{jobId}/prompts/generate` | `GeneratePromptCommand` | Authenticated |
| POST | `/api/jobs/{jobId}/prompts/{promptId}/approve-production` | `ApprovePromptForProductionCommand` | Authenticated |

**New Domain Entities**:
- `ScoringPrompt` in `Domain/Entities/`
- `PromptStatus` enum in `Domain/Enums/`
- `PromptSource` enum in `Domain/Enums/`
- `IScoringPromptRepository` in `Domain/Interfaces/`

**New Application Layer**:
- `Application/Prompts/Commands/`: `CreatePromptCommand`, `EditPromptCommand`, `ActivatePromptCommand`, `RatePromptCommand`, `GeneratePromptCommand`, `ApprovePromptForProductionCommand`
- `Application/Prompts/Queries/`: `GetPromptsQuery`, `GetPromptQuery`

**New Infrastructure**:
- `ScoringPromptRepository` in `Infrastructure/Persistence/Repositories/`
- EF Core migration for `ScoringPrompts` table
- DbContext configuration for `ScoringPrompt` entity

---

## Prompt Test Runs *(NEW — US3a)* — `PromptTestRunEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| POST | `/api/jobs/{jobId}/prompts/{promptId}/test-runs` | `CreatePromptTestRunCommand` | Authenticated |
| GET | `/api/jobs/{jobId}/prompts/{promptId}/test-runs` | `GetPromptTestRunsQuery` | Authenticated |
| GET | `/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}` | `GetPromptTestRunQuery` | Authenticated |
| POST | `/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}/approve` | `ApprovePromptTestRunCommand` | Authenticated |

**New Domain Entities**:
- `PromptTestRun` in `Domain/Entities/`
- `TestRunStatus` enum in `Domain/Enums/`
- `IPromptTestRunRepository` in `Domain/Interfaces/`

**New Application Layer**:
- `Application/Prompts/Commands/`: `CreatePromptTestRunCommand`, `ApprovePromptTestRunCommand`
- `Application/Prompts/Queries/`: `GetPromptTestRunsQuery`, `GetPromptTestRunQuery`

**New Infrastructure**:
- `PromptTestRunRepository` in `Infrastructure/Persistence/Repositories/`
- EF Core migration for `PromptTestRuns` table

---

## Applications — `ApplicationEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| POST | `/api/jobs/{jobId}/applications/upload` | `UploadApplicationsCommand` | Authenticated |
| GET | `/api/jobs/{jobId}/applications` | `GetApplicationsQuery` | Authenticated |
| GET | `/api/applications/{applicationId}` | `GetApplicationDetailQuery` | Authenticated |
| GET | `/api/applications/{applicationId}/runs` | `GetScoringRunsQuery` | Authenticated |
| GET | `/api/applications/{applicationId}/result` | `GetAggregatedResultQuery` | Authenticated |
| GET | `/api/applications/{applicationId}/extraction` | `GetExtractionArtifactQuery` | Authenticated |
| GET | `/api/applications/{applicationId}/manual-review` | `GetManualReviewQuery` | Authenticated |
| POST | `/api/applications/{applicationId}/manual-review` | `SaveManualReviewCommand` | Authenticated |

**Change**: `GetApplicationsQuery` must exclude applications with `testRunId != null` from production list results.

---

## System Operations

### Stats — `StatsEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| GET | `/api/stats` | `GetStatsQuery` | Authenticated |

### DLQ — `DlqEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| GET | `/api/dlq` | `GetDlqItemsQuery` | Admin |
| POST | `/api/dlq/{itemId}/retry` | `RetryDlqItemCommand` | Admin |

### Audit — `AuditEndpoints.cs`

| Method | Path | MediatR Handler | Auth |
|--------|------|------------------|------|
| GET | `/api/audit` | `GetAuditEventsQuery` | Authenticated |

---

## EF Core Migration Plan

New migration required for US3a entities:

```text
Migration: AddScoringPromptsAndTestRuns
  - Add table: ScoringPrompts (PromptId PK, JobId FK, VersionNumber, PromptText, Status, ...)
  - Add table: PromptTestRuns (TestRunId PK, JobId FK, PromptId FK, Status, ApplicationIdsJson, ...)
  - Add column: Applications.TestRunId (nullable FK → PromptTestRuns)
  - Add index: IX_ScoringPrompts_JobId_Status (for single-active-per-job queries)
  - Add index: IX_Applications_TestRunId (for filtering test cases)
```

---

## Blazor WASM Client Changes

New pages and components for US3a:

| Component | Purpose | Route |
|-----------|---------|-------|
| `PromptManagement.razor` | Prompt list/create/edit for a job | Embedded in `JobDetail.razor` |
| `PromptEditor.razor` | Rich text editor for prompt content | Child of PromptManagement |
| `PromptTestRunner.razor` | Upload test apps, view test results | Child of PromptManagement |
| `PromptRevisionList.razor` | Dropdown/list of all revisions | Child of PromptManagement |

Client-side service additions:
- `ApiClient.cs`: Add methods for all prompt and test-run endpoints
- `BrowserRequestCredentials.Include` via `DelegatingHandler` (FR-021)
