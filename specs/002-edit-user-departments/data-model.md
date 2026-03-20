# Data Model — 002-edit-user-departments

## Existing Entity: User (no changes)

The `User` entity already contains all fields needed for this feature. No migration required.

| Field | Type | Notes |
|-------|------|-------|
| Id | `string` | GUID, primary key |
| Username | `string` | Unique, immutable (not editable) |
| Role | `string` | `"admin"` or `"recruiter"` |
| FullName | `string` | Editable |
| Email | `string` | Editable, must be unique across users |
| Department | `string` | Editable, comma-separated for multi-department (e.g. `"Engineering,HR"`) |
| PasswordHash | `string` | Not exposed in update — separate password management |
| CreatedAt | `DateTime` | Immutable |
| LastLogin | `DateTime?` | System-managed |

### Department Field Behavior

- **Storage format**: Comma-separated string, no spaces around delimiter
  - Single: `"Engineering"`
  - Multiple: `"Engineering,HR,Marketing"`
  - None: `""` (empty string)
- **Validation**: Individual department names must not contain commas
- **Display**: Split by `,` into individual tags in the UI
- **Save**: Join tags with `,` before sending to API
- **Existing compatibility**: `GetJobsQuery` already splits `Department` by `,` for RBAC filtering — no changes needed

## New DTOs / Records

### UpdateUserRequest (endpoint record)

```csharp
public record UpdateUserRequest(
    string FullName,
    string Email,
    string Role,
    string Department  // comma-separated, e.g. "Engineering,HR"
);
```

### UpdateUserCommand (MediatR command)

```csharp
public record UpdateUserCommand(
    string UserId,
    string FullName,
    string Email,
    string Role,
    string Department
) : IRequest<bool>;
```

## Validation Rules

| Field | Rule | Error Message |
|-------|------|---------------|
| FullName | Not empty, max 200 chars | "Full name is required" / "Full name must not exceed 200 characters" |
| Email | Not empty, valid email format | "Email is required" / "Email must be a valid email address" |
| Role | Must be `"admin"` or `"recruiter"` | "Role must be 'admin' or 'recruiter'" |
| Department | Each comma-separated value must not contain commas (inherent), max 100 chars per name | "Department names must not exceed 100 characters" |
| UserId | Not empty | "User ID is required" |

## State Transitions

None — this feature is a simple CRUD update. No state machine or workflow involved.
