# Quickstart — 002-edit-user-departments

## Prerequisites

- .NET 9+ SDK installed
- Repository cloned, on branch `002-edit-user-departments`
- SQLite database initialized (runs automatically on first `dotnet run`)

## Build & Run

```bash
cd dotnet
dotnet run --project src/Web.Server/TalentMatch.Web.Server.csproj
```

The app starts at `https://localhost:5001` (or the port configured in `launchSettings.json`).

## Verify the Feature

### 1. Log in as admin

Default admin credentials are seeded on first run. Check `src/Infrastructure/Persistence/SeedData.cs`
or the `.env` file for the admin username/password.

### 2. Navigate to User Management

Click the "Users" link in the sidebar/navigation.

### 3. Edit a user

- Click the **Edit** button on any user row → modal opens with pre-populated fields
- Modify the full name, email, or role
- In the department field, type a department name and press Enter → appears as a tag
- Click × on a tag to remove it
- Click **Save** → modal closes, table updates immediately

### 4. Test validation

- Clear the full name field → inline error appears
- Enter an invalid email → inline error appears
- Add a department with a comma → validation error

### 5. Test authorization

- Log in as a non-admin user → Edit button should not be visible
- Attempt `PUT /api/users/{id}` directly → 403 Forbidden

## Run Tests

```bash
cd dotnet
dotnet test tests/Application.Tests
```
