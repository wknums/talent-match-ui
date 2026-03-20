# Research — 002-edit-user-departments

## R1: Existing Update Infrastructure

**Decision**: Use `IUserRepository.UpdateAsync(User, CancellationToken)` — already defined and implemented.  
**Rationale**: The repository interface already has an `UpdateAsync` method. The EF Core `UserRepository`
implements it. No new storage layer code is needed.  
**Alternatives considered**: None — using the existing interface is the obvious choice.

## R2: Command/Handler Pattern for UpdateUser

**Decision**: Mirror `CreateUserCommand` pattern — single `record` command with a collocated handler class
in the same file.  
**Rationale**: All existing commands (`CreateUserCommand`, `DeleteUserCommand`, `ChangePasswordCommand`)
follow this exact pattern: `record` + `Handler` in one `.cs` file, injecting `IUserRepository` and
(where needed) `ICurrentUserService`.  
**Alternatives considered**: Separate files for command and handler — rejected because the codebase
convention collocates them.

## R3: Validator Pattern

**Decision**: Create `UpdateUserValidator` as `AbstractValidator<UpdateUserCommand>` in a separate file,
mirroring `CreateUserValidator`.  
**Rationale**: `CreateUserValidator` follows FluentValidation conventions with a separate validator file.
The validation pipeline is already wired in `DependencyInjection.cs` via assembly scanning.  
**Alternatives considered**: Inline validation in the handler — rejected because FluentValidation pipeline
is already configured and catches errors before the handler runs.

## R4: Email Uniqueness Check

**Decision**: Validate email uniqueness in the `UpdateUserCommandHandler` (not the validator), throwing
`InvalidOperationException` if duplicate found.  
**Rationale**: `CreateUserCommandHandler` validates username uniqueness the same way — in the handler
after the validator runs, because uniqueness requires a database query. The validator handles format-only
checks.  
**Alternatives considered**: Custom FluentValidation rule with injected repository — adds complexity
for no benefit; handler validation is the established pattern here.

## R5: Department Comma Storage Format

**Decision**: Keep comma-separated string format. Frontend splits/joins on display/save.  
**Rationale**: The `User.Department` field is already a `string`. `GetJobsQuery` already splits by comma
for RBAC filtering. No schema change required.  
**Alternatives considered**: JSON array in string field — rejected because it would break existing
`GetJobsQuery` comma-split logic and existing data.

## R6: Self-Role-Change Prevention

**Decision**: Check `ICurrentUserService.UserId` in the handler. If the current user is editing themselves,
silently ignore any role change (keep existing role).  
**Rationale**: The spec requires admins can edit their own profile but cannot change their own role. The
`DeleteUserCommand` already uses `ICurrentUserService` for self-action prevention. Silent ignore (rather
than error) provides better UX — the admin can still save other field changes.  
**Alternatives considered**: Return error if self-role-change attempted — rejected because it forces the
admin to clear the role field to save other changes, which is confusing.

## R7: Blazor Modal vs Inline Edit

**Decision**: Use a modal dialog (same pattern as the existing delete confirmation and password reset
modals in `UserManagement.razor`).  
**Rationale**: The component already has modal patterns for delete confirmation and password reset. A modal
keeps the table layout stable and provides a clear edit/cancel flow. Constitution requires dialogs to be
resizable and draggable with scroll bars.  
**Alternatives considered**: Inline row editing — rejected because it would require significant table
restructuring and doesn't match the existing UX patterns.

## R8: Tag-Style Department Input in Blazor

**Decision**: Custom inline Blazor component within the edit modal — an `<input>` with `@onkeydown`
handler for Enter key, rendering tags as styled `<span>` elements with × buttons.  
**Rationale**: No external tag-input component is needed. The implementation is ~30 lines of Razor markup
and ~15 lines of `@code`. Keeps dependencies minimal (YAGNI).  
**Alternatives considered**: MudBlazor `MudChipSet` — rejected because MudBlazor is not yet adopted in
the project (the constitution marks it as "TBD at project start").

## R9: Authorization on PUT Endpoint

**Decision**: Add the PUT route to the existing `group` in `UsersEndpoints.cs` which already has
`.RequireAuthorization("AdminOnly")`.  
**Rationale**: The `MapGroup("/api/users")` already applies the `AdminOnly` policy to all routes in the
group. Adding a new `MapPut` to the same group automatically inherits the policy.  
**Alternatives considered**: None — this is the correct approach.
