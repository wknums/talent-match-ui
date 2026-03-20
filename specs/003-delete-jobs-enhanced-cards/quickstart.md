# Quickstart — 003-delete-jobs-enhanced-cards

## Prerequisites

- .NET 9+ SDK installed
- Repository cloned, on branch `003-delete-jobs-enhanced-cards`
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

### 2. Verify enhanced job cards on Dashboard

- Navigate to the Dashboard (home page)
- Each job card should show:
  - **Creator name**: "Created by Jane Doe" (or "Unknown User" for orphaned jobs)
  - **Creation date**: "Created Mar 5, 2026" (human-friendly format)
  - **Completion progress**: "3 / 5 completed" (or "0 applications" if none)
  - All existing info: title, organisation, department, days posted, status badge

### 3. Delete a job with no applications (Dashboard)

- On the Dashboard, click the **delete button** (🗑️) on a job card with 0 applications
- Confirmation dialog appears with the job title
- Click **Confirm** → job card disappears from the Dashboard without page reload
- Refresh the page → job is gone

### 4. Delete a job with applications (Dashboard)

- On the Dashboard, click delete on a job card that has applications
- Confirmation dialog appears with the job title AND a cascade warning:
  "This will also permanently delete X applications and all associated scoring data."
- Click **Confirm** → job and all applications removed
- Navigate to the job's old URL → 404 or "not found" message

### 5. Delete from Job Detail page

- Navigate to a job's detail page (`/jobs/{jobId}`)
- Click the **Delete** button in the page actions
- Confirm in the dialog → redirected back to Dashboard, job is gone

### 6. Cancel deletion

- Click delete on any job → dialog appears
- Click **Cancel** → dialog closes, job unchanged

### 7. Test authorization (non-admin)

- Log in as a recruiter user
- Dashboard job cards should NOT show any delete button
- Job Detail page should NOT show a delete button
- Attempt `DELETE /api/jobs/{id}` directly → 403 Forbidden

## Run Tests

```bash
cd dotnet
dotnet test tests/Application.Tests
```
