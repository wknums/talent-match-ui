# Authentication System

The app supports two authentication modes:

- `APP_AUTH_MODE=local` → username/password login (default for local development)
- `APP_AUTH_MODE=entra` → Microsoft Entra ID login with role authorization from the app `Users` table

## Entra Login Flow

1. User signs in through App Service Authentication (`/.auth/login/aad`).
2. Backend reads `x-ms-client-principal` claims.
3. Backend finds a matching user record by `username` or `email` (case-insensitive).
4. If a matching record exists, login succeeds and role-based access is applied.
5. If no matching record exists, login is rejected with unauthorized-role message.

## Admin Seeding for Azure DB Owner

When `APP_AUTH_MODE=entra`, startup seeds or upgrades one admin user using:

- `ENTRA_ADMIN_USERNAME` (required)
- `ENTRA_ADMIN_FULL_NAME` (optional)
- `ENTRA_ADMIN_EMAIL` (optional)

This guarantees the configured Azure DB owner identity can access the app as `admin` on first login.

## Assigning Roles to Other Users

Add or update entries in the `Users` table so each user’s `username` or `email` matches their Entra UPN/email, and set `Role` to:

- `admin`
- `recruiter`
- `business_panel`

You can do this either:

- Through the app’s **Manage Users** UI (as an admin), or
- Directly in Azure SQL (for bootstrap/bulk setup).

## QA Tenant Profile Guidance

For your tenant-specific profile (for example `.env_qa_mcaps`), ensure it includes:

- `AZURE_SUBSCRIPTION_ID=<new subscription>`
- `AZURE_TENANT_ID=<new tenant>`
- `APP_AUTH_MODE=entra`
- `ENTRA_ADMIN_USERNAME=<azure-db-owner-upn>`

Deploy with:

```bash
./infra/scripts/deploy.sh .env_qa_mcaps test apply both
```

## Local Mode Defaults

In local mode, startup seeds:

- Username: `admin`
- Password: `adm1n99`

Change this password immediately for any shared environment.
