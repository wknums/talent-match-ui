# Contract: PUT /api/users/{userId}

## Endpoint

```
PUT /api/users/{userId}
Authorization: Required (AdminOnly policy)
Content-Type: application/json
```

## Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `userId` | `string` | The GUID ID of the user to update |

## Request Body

```json
{
  "fullName": "Jane Doe",
  "email": "jane.doe@example.com",
  "role": "recruiter",
  "department": "Engineering,HR"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fullName` | `string` | Yes | Non-empty, max 200 chars |
| `email` | `string` | Yes | Non-empty, valid email format, unique across users |
| `role` | `string` | Yes | `"admin"` or `"recruiter"` |
| `department` | `string` | No | Comma-separated department names; each name max 100 chars, no commas in names; empty string allowed |

## Responses

### 200 OK

User updated successfully. No response body.

### 400 Bad Request

Validation failure. Body contains FluentValidation error details.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "FullName": ["Full name is required"]
  }
}
```

### 404 Not Found

User with the given `userId` does not exist.

### 409 Conflict

Email address already in use by another user.

```json
"Email 'jane@example.com' is already in use by another user."
```

### 403 Forbidden

Caller does not have the Admin role (handled by authorization policy).

## Behavior Notes

- **Username is immutable** — not included in the update payload.
- **Password is not part of this endpoint** — managed via separate reset/change endpoints.
- **Self-role-change prevention**: If the authenticated admin is editing their own user record, the `role` field is silently ignored (existing role preserved). All other fields are updated normally.
- **Department format**: The client joins tags with `,` before sending. The server stores the value as-is.
- **Last-write-wins**: No optimistic concurrency. If two admins edit the same user, the last save wins.
