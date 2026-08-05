# Authentication System

## Overview

The Talent Matching Platform ships with two mutually exclusive authentication modes selected by the
`APP_AUTH_MODE` setting. Both stacks (Stack A Node/React and Stack B .NET/Blazor) read the same
setting and expose the same canonical error surface.

| Mode | `APP_AUTH_MODE` | Identity source | Intended use |
| --- | --- | --- | --- |
| Simple | `simple` (default) | Local username/password records | Demos and offline development |
| Entra | `entra` | Microsoft Entra ID, single tenant | Shared and production environments |

Switching modes is a configuration change only. Simple-mode credentials are never accepted in Entra
mode, and Entra tokens are never accepted in simple mode.

## Entra Mode

### Sign-in flow

- Single-tenant Entra ID. Both SPAs are public clients using authorization code flow with PKCE and
  hold no client secret.
- One protected API registration exposes the delegated scope `access_as_user`. Only the two
  registered SPA client IDs may request it.
- Access tokens older than 15 minutes are rejected with `token_stale`. The client performs exactly
  one silent refresh before surfacing the error.
- Microsoft Graph is never called on a request path. All authorization state is resolved from the
  application database.

### Application roles

| Role | Scope shape | Capability |
| --- | --- | --- |
| `admin` | Global (no organization or department) | Full access, access management, failure queue |
| `organization_admin` | Organization only | Manage jobs and access within one organization |
| `recruiter` | Organization + department | Manage jobs within one department |
| `business_panel` | Organization, department optional | Read-only review access |

A token `roles` claim never grants access on its own. Effective authorization always comes from the
persisted role assignment, organization membership, and department membership records.

### Pending profiles

The first successful sign-in by an identity that has no active assignment creates a pending profile
record (tenant ID, object ID, username, display name, email) and returns `assignment_missing` (403).
The pending profile exists only so an administrator can find and assign the person; it grants no
access. Once an assignment is added the same identity signs in normally without re-registration.

### Access management

In Entra mode the `/api/access-management` endpoints replace local user administration:

| Operation | Endpoint | Allowed actors |
| --- | --- | --- |
| List identities | `GET /api/access-management/users` | `admin`, `organization_admin` |
| Inspect one identity | `GET /api/access-management/users/{objectId}` | `admin`, `organization_admin` |
| Grant or update organization access | `PUT /api/access-management/users/{objectId}/organizations/{organizationId}` | `admin`, `organization_admin` (own organization) |
| Revoke a role assignment | `DELETE /api/access-management/users/{objectId}/organizations/{organizationId}/role-assignments/{assignmentId}` | `admin`, `organization_admin` (own organization) |

Mutations require the caller's `expectedVersion` query value to match the stored authorization
version. A stale value fails with `version_conflict` (409) so concurrent administrators cannot
overwrite each other.

### Explicit default department

An organization membership must name exactly one department membership as its default. The default
is never inferred:

- Granting organization access requires the caller to choose the default department membership.
- Removing the department membership that is currently the default is rejected until a replacement
  default is supplied in the same request.
- A membership whose default department membership is missing or inactive resolves to
  `membership_missing` (403) rather than silently picking another department.

### Revocation

- Removing a role assignment increments the identity's authorization version, which invalidates any
  cached authorization context on the next request.
- Deactivating the identity yields `identity_disabled` (403); removing every assignment yields
  `assignment_missing` (403); an assignment that exists but is inactive yields `assignment_revoked`.
- Revocation never deletes the Entra directory object. It only removes TalentMatch authorization
  state.
- Every grant, change, and revocation writes an immutable audit event with actor, action, target,
  correlation ID, and timestamp.

### Canonical sign-in errors

| Code | Status | Meaning |
| --- | --- | --- |
| `auth_required` | 401 | No usable token was presented |
| `invalid_token` | 401 | Token failed validation |
| `wrong_tenant` | 401 | Token came from a different tenant |
| `invalid_audience` | 401 | Token was not issued for the TalentMatch API |
| `unauthorized_client` | 401 | Calling client is not a registered SPA |
| `token_stale` | 401 | Token older than 15 minutes |
| `role_missing` / `role_conflict` | 403 | No supported role, or roles that cannot be reconciled |
| `assignment_missing` / `assignment_revoked` | 403 | No active application access |
| `membership_missing` | 403 | No active organization or department membership |
| `scope_unmapped` | 403 | Group mapping does not resolve to an active scope |
| `identity_disabled` | 403 | Application identity is disabled |

Operator-facing setup and recovery procedures live in `docs/ENTRA_AUTHORIZATION.md`.

## Simple Mode

Simple mode keeps the original username/password system for demos and offline development. It is
selected when `APP_AUTH_MODE` is unset or set to `simple`. None of the sections below apply in Entra
mode.

## Default Admin Account

On first launch, the system creates a default admin account:
- **Username**: `admin`
- **Password**: ``adm1n99

**Important**: Change this password immediately after first login.

## User Roles

### Admin
- **Access**: Can view and manage all jobs across all departments
- **Capabilities**:
  - Create, edit, and manage all jobs
  - Create new user accounts
  - Reset passwords for any user
  - Delete users (except themselves)
  - Approve or reject password reset requests from recruiters
  - View pending password reset requests

### Recruiter
- **Access**: Can only view jobs from their assigned department
- **Capabilities**:
  - Create and edit jobs within their department
  - Upload and review applications for their jobs
  - Perform manual reviews
  - Change their own password
  - Request password reset from admin

## User Management (Admin Only)

Admins can access the User Management interface from the user menu:

1. **Create Users**:
   - Click "Add User" button
   - Fill in username, full name, email (optional), department (optional), and role
   - Set initial password (minimum 6 characters)
   - User can change password after first login

2. **Reset Passwords**:
   - Click "Reset Password" next to any user
   - Enter new password (minimum 6 characters)
   - Password is immediately updated

3. **Delete Users**:
   - Click delete icon next to any user
   - Confirm deletion
   - Note: Cannot delete your own admin account

4. **Password Reset Requests**:
   - Pending requests appear at the top of the User Management dialog
   - Shows requester name, username, and request timestamp
   - Approve: Enter new password for the user
   - Reject: Dismiss the request

## Self-Service Password Management

All users can change their own password:

1. Click on your user menu (top right)
2. Select "Change Password"
3. Enter current password
4. Enter and confirm new password (minimum 6 characters)
5. Submit

## Password Reset Request (Recruiters)

If a recruiter forgets their password:

1. Click on user menu
2. Select "Request Password Reset"
3. Admin will be notified of the request
4. Admin can approve and set a new password
5. User will be able to log in with the new password

## Data Storage

All authentication data is stored using the Spark KV persistence API:
- Passwords are hashed using SHA-256
- User data persists between sessions
- Password reset requests are tracked until resolved

## Security Notes

- Minimum password length: 6 characters
- Passwords are hashed before storage
- Old password required for self-service password changes
- Admin role required for user management operations
- Users cannot delete themselves
- Session persists in browser storage

## Migration From Simple Mode to Entra Mode

1. Provision the Entra registrations and role groups described in `docs/ENTRA_AUTHORIZATION.md`.
2. Apply the shared schema changes so organization, department, membership, and role assignment
   tables exist in both stacks.
3. Bootstrap the first administrator with `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`.
4. Set `APP_AUTH_MODE=entra` on both stacks and restart.
5. Have each remaining person sign in once to create a pending profile, then assign access through
   `/api/access-management`.

After the switch, local password endpoints are no longer mapped: password changes, resets, and reset
requests are handled by Entra ID.

## Example Usage

### Creating a Recruiter for Engineering Department

As admin:
1. Open User Management
2. Click "Add User"
3. Enter:
   - Username: `john.doe`
   - Full Name: `John Doe`
   - Email: `john.doe@company.com`
   - Department: `Engineering`
   - Role: `Recruiter`
   - Password: `temp123`
4. John can now log in and will only see Engineering jobs

### Approving a Password Reset

As admin:
1. Open User Management
2. See pending request from user
3. Click "Approve"
4. Enter new password in prompt
5. User can now log in with new password
