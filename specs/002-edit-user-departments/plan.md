# Implementation Plan: Edit User with Multi-Department Assignment

**Branch**: `002-edit-user-departments` | **Date**: 2026-03-10 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/002-edit-user-departments/spec.md`
**Constitution**: v1.1.0

---

## Summary

Add the ability for admins to edit existing users (full name, email, role, department) with
multi-department assignment via a tag-style input. Departments are stored as a comma-separated
string in the existing `User.Department` field — no schema migration needed.

**Scope**: Both stacks.
- **Stack B** (.NET Blazor WASM / Clean Architecture) — **COMPLETED**
- **Stack A** (Node.js/Express + React/TypeScript) — implementation pending

**Approach (Stack B — completed)**:
- Backend: `UpdateUserCommand` + `UpdateUserValidator` → `PUT /api/users/{userId}` endpoint
- Frontend: `UpdateUserAsync` in `ApiClient` → Edit button + modal form in `UserManagement.razor`
- Department UI: tag-style input (type + Enter to add, click × to remove)
- Self-role-change prevention: admins can edit their own profile but not their own role

**Approach (Stack A)**:
- Backend: Add `PUT /:userId` route to `server/routes/users.ts` with `requireRole('admin')` middleware
  — load users from KV store (`AUTH_USERS`), find by userId, validate email uniqueness, apply changes,
  write back to KV store. Self-role-change prevention mirrors Stack B.
- Frontend: Add `updateUser()` to `src/lib/api-real.ts` → call `PUT /api/users/{userId}`
- Frontend: Add `updateUser()` proxy to `src/lib/api.ts` (mock/real bridge)
- Frontend: Add edit button + inline edit form (or modal) to `src/components/UserManagementDialog.tsx`
  — tag-style multi-department input using existing `Badge` + `Input` components
- Types: `User.department` in `src/types/index.ts` is already `string` — compatible with comma-separated format

## Technical Context

### Stack B (.NET — completed)
**Language/Version**: C# / .NET 9+, Blazor WebAssembly  
**Primary Dependencies**: MediatR (CQRS), FluentValidation, Entity Framework Core  
**Storage**: SQLite (local dev via EF Core) — existing `User` table, no migration needed  
**Testing**: xUnit + FluentAssertions  
**Target Platform**: Web browser (Blazor WASM hosted model)  
**Project Type**: Full-stack web application (Blazor WASM + ASP.NET Core minimal API)  
**Performance Goals**: Edit save completes in <1s  
**Constraints**: Department field is `string` (comma-separated); department names must not contain commas  
**Scale/Scope**: Single feature — 1 new command, 1 new validator, 1 new endpoint, 1 updated API client method, 1 updated Razor component

### Stack A (Node.js/React — pending)
**Language/Version**: TypeScript / Node.js 20+, React 18  
**Primary Dependencies**: Express (routing), React (UI), shadcn/ui components, sonner (toasts)  
**Storage**: KV store (key-value JSON arrays in Azure SQL or local) — users stored as JSON array under `AUTH_USERS` key  
**Testing**: Vitest  
**Target Platform**: Web browser (Vite dev server + Express API proxy)  
**Project Type**: Full-stack web application (Express API + React SPA)  
**Performance Goals**: Edit save completes in <1s  
**Constraints**: Same comma-separated department format; KV store requires read-modify-write pattern (`getArray` → mutate → `setArray`); `requireRole('admin')` middleware for authz  
**Scale/Scope**: 1 new route handler in `users.ts`, 1 new API method in `api-real.ts`, 1 proxy method in `api.ts`, 1 updated React component (`UserManagementDialog.tsx`)

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

### Source Code — Stack B (files to create or modify) — COMPLETED

```text
dotnet/src/
├── Application/Users/Commands/
│   ├── UpdateUserCommand.cs         # NEW — command + handler
│   └── UpdateUserValidator.cs       # NEW — FluentValidation rules
├── Web.Server/Endpoints/
│   └── UsersEndpoints.cs            # MODIFY — add PUT /{userId} route + UpdateUserRequest record
└── Web.Client/
    ├── Services/
    │   └── ApiClient.cs             # MODIFY — add UpdateUserAsync method
    └── Components/
        └── UserManagement.razor     # MODIFY — add Edit button, edit modal, tag-style department input

dotnet/tests/
└── Application.Tests/Users/
    └── UpdateUserCommandTests.cs    # NEW — unit tests for update handler + validator
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
Express route + React component patterns (Stack A). All new files go into the same directories
as their Create/Delete counterparts. No new projects or layers required.
