# Implementation Plan: Edit User with Multi-Department Assignment

**Branch**: `002-edit-user-departments` | **Date**: 2026-03-10 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/002-edit-user-departments/spec.md`
**Constitution**: v1.1.1

---

## Summary

Add the ability for admins to edit existing users (full name, email, role, department) with
multi-department assignment via a tag-style input. Departments are stored as a comma-separated
string in the existing `User.Department` field — no schema migration needed.

**Scope**: Both stacks.
- **Stack A** (Node.js/Express + React/TypeScript) — implemented
- **Stack B** (.NET Blazor WASM / Clean Architecture) — implemented; parity re-review pending

**Approach (Stack B — current state)**:
- Backend: `UpdateUserCommand` + `UpdateUserValidator` exist in the Application layer
- Frontend client: `UpdateUserAsync` exists in `ApiClient` and now surfaces problem-details style validation errors cleanly
- Web/API surface: `UsersEndpoints.cs` exposes `PUT /api/users/{userId}`, and `UserManagement.razor` now includes the edit-user modal, pre-populated fields, and multi-department tag input
- Self-role-change prevention logic remains enforced in `UpdateUserCommand` and is now reachable through the Blazor UI and API surface

**Approach (Stack A — implemented)**:
- Backend: `server/routes/users.ts` exposes `PUT /:userId` with admin RBAC, email uniqueness validation, and self-role-change protection
- Frontend: `updateUser()` exists in `src/lib/api-real.ts` and is exposed through `src/lib/api.ts`
- Frontend: `src/components/UserManagementDialog.tsx` includes edit controls and multi-department tag input
- Types: `User.department` in `src/types/index.ts` remains compatible with comma-separated storage

## Technical Context

### Stack B (.NET — implemented; parity re-review pending)
**Language/Version**: C# / .NET 9+, Blazor WebAssembly  
**Primary Dependencies**: MediatR (CQRS), FluentValidation, Entity Framework Core  
**Storage**: SQLite (local dev via EF Core) — existing `User` table, no migration needed  
**Testing**: xUnit + FluentAssertions  
**Target Platform**: Web browser (Blazor WASM hosted model)  
**Project Type**: Full-stack web application (Blazor WASM + ASP.NET Core minimal API)  
**Performance Goals**: Edit save completes in <1s  
**Constraints**: Department field is `string` (comma-separated); department names must not contain commas  
**Scale/Scope**: Single feature — existing command/validator foundation plus completed endpoint, Razor UI, client error handling, and focused application/web test coverage

### Stack A (Node.js/React — implemented)
**Language/Version**: TypeScript / Node.js 20+, React 18  
**Primary Dependencies**: Express (routing), React (UI), shadcn/ui components, sonner (toasts)  
**Storage**: KV store (key-value JSON arrays in Azure SQL or local) — users stored as JSON array under `AUTH_USERS` key  
**Testing**: Vitest  
**Target Platform**: Web browser (Vite dev server + Express API proxy)  
**Project Type**: Full-stack web application (Express API + React SPA)  
**Performance Goals**: Edit save completes in <1s  
**Constraints**: Same comma-separated department format; KV store requires read-modify-write pattern (`getArray` → mutate → `setArray`); `requireRole('admin')` middleware for authz  
**Scale/Scope**: Implemented with 1 route handler in `users.ts`, 1 API method in `api-real.ts`, 1 proxy method in `api.ts`, and 1 updated React component (`UserManagementDialog.tsx`)

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| II. Layered Architecture (Stack B) | ✅ PASS | Domain unchanged; Application gets UpdateUserCommand; Infrastructure untouched; Presentation gets endpoint + Razor UI |
| III. Storage Abstraction | ✅ PASS | Uses existing `IUserRepository.UpdateAsync` — no new storage implementation needed |
| IV. Security Defaults | ✅ PASS | Admin-only endpoint (existing `AdminOnly` policy); self-role-change prevented; no password in update payload |
| VI. UI Precision | ✅ PASS | Loading state on save, error toast, inline validation, immediate table refresh |
| VII. YAGNI | ✅ PASS | No new abstractions — reuses existing patterns (command/handler/validator/endpoint) |
| IX. Clean Architecture | ✅ PASS | Dependencies flow inward: Presentation → Application → Domain. No shortcuts. |

**Gate result**: ALL PASS — no violations.

---

## Project Structure

### Documentation (this feature)

```text
specs/002-edit-user-departments/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (PUT /api/users/{userId} contract)
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code — Stack B (current state)

```text
dotnet/src/
├── Application/Users/Commands/
│   ├── UpdateUserCommand.cs         # EXISTS — command + handler
│   └── UpdateUserValidator.cs       # EXISTS — FluentValidation rules
├── Web.Server/Endpoints/
│   └── UsersEndpoints.cs            # IMPLEMENTED — PUT /{userId} route + UpdateUserRequest record
└── Web.Client/
    ├── Services/
    │   └── ApiClient.cs             # IMPLEMENTED — UpdateUserAsync + improved error extraction for validation/problem details
    └── Components/
        └── UserManagement.razor     # IMPLEMENTED — Edit button, edit modal, tag-style department input

dotnet/tests/
├── Application.Tests/               # IMPLEMENTED — UpdateUserCommand coverage
└── Web.Tests/                       # IMPLEMENTED — component + integration coverage for edit-user flow
```

### Source Code — Stack A (files to modify)

```text
server/
└── routes/
    └── users.ts                     # MODIFY — add PUT /:userId route with requireRole('admin')

src/
├── lib/
│   ├── api-real.ts                  # MODIFY — add updateUser() method
│   └── api.ts                       # MODIFY — add updateUser() to proxy/mock bridge
├── components/
│   └── UserManagementDialog.tsx     # MODIFY — add Edit button, edit form, tag-style department input
└── types/
    └── index.ts                     # NO CHANGE — User.department is already string (comma-separated compatible)

tests/
└── unit/
    └── user-update.test.ts          # NEW — unit tests for update route handler
```

**Structure Decision**: Follows the existing Clean Architecture layout (Stack B) and existing
Express route + React component patterns (Stack A). Both stacks now implement the planned surface; parity still requires checklist re-review rather than assumption. No new projects or layers required.
