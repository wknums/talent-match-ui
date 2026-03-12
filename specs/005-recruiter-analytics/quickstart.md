# Quickstart — 005-recruiter-analytics

**Feature**: Recruiter Analytics Dashboard  
**Date**: 2026-03-12

## Prerequisites

- Node.js 20+ and npm installed
- .NET 9 SDK installed (for Stack B work)
- Repository cloned and on branch `005-recruiter-analytics`

## Stack A Setup

```bash
# Install dependencies
npm install

# Start dev server (Express backend + Vite frontend)
npm run dev

# Run tests
npm test

# Run tests in watch mode
npm run test:watch
```

**Default API mode**: `mock` — the frontend uses `src/lib/api-mock.ts` which returns hardcoded analytics data. Switch to `real` mode by setting `API_MODE=real` in `.env` to use the backend endpoints.

**Key files to modify**:

| File | What to do |
|------|------------|
| `server/routes/stats.ts` | Add `GET /recruiters` and `GET /departments` sub-routes with RBAC |
| `src/lib/api-real.ts` | Implement `getRecruiterAnalytics()` and `getDepartmentAnalytics()` to call new endpoints |
| `src/components/AnalyticsView.tsx` | Minor updates if needed for role-scoped stat cards |
| `tests/unit/analytics.test.ts` | New test file for analytics computation logic |

## Stack B Setup

```bash
cd dotnet

# Restore packages
dotnet restore

# Build solution
dotnet build

# Run tests
dotnet test

# Run the web server
dotnet run --project src/Web.Server
```

**Key files to create**:

| File | Layer | Purpose |
|------|-------|---------|
| `src/Domain/Entities/RecruiterAnalytics.cs` | Domain | Immutable record type |
| `src/Domain/Entities/DepartmentAnalytics.cs` | Domain | Immutable record type |
| `src/Application/Analytics/Queries/GetRecruiterAnalyticsQuery.cs` | Application | CQRS query + handler |
| `src/Application/Analytics/Queries/GetDepartmentAnalyticsQuery.cs` | Application | CQRS query + handler |
| `src/Web.Server/Endpoints/AnalyticsEndpoints.cs` | Presentation | Minimal API endpoints |
| `src/Web.Client/Pages/Analytics.razor` | Presentation | Blazor page |

## Testing the Feature

### Manual testing (Stack A)

1. Start with `npm run dev`
2. Log in as admin (default: `admin` / `admin123`)
3. Click "View Analytics" button in dashboard header
4. Verify 4 stat cards display (mock mode shows sample data)
5. Check "By Recruiter" tab shows table with department filter
6. Check "By Department" tab shows department cards with nested tables
7. Click "← Back to Dashboard" to return

### Manual testing (RBAC)

1. Log in as `recruiter` role — should see only own department's data
2. Log in as `business_panel` role — "View Analytics" button should NOT appear
3. With `API_MODE=real`, call `GET /api/stats/recruiters` without auth — expect 401
4. With `business_panel` session, call `GET /api/stats/recruiters` — expect 403

### Automated tests

```bash
# Stack A unit tests
npm test -- --grep analytics

# Stack A integration tests  
npm test -- tests/integration/api.test.ts

# Stack B tests
cd dotnet && dotnet test
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `API_MODE` | `mock` | Set to `real` to use backend analytics endpoints |
| `STORAGE_PROVIDER` | `local` | Storage backend (`local` or `azuresql`) |
| `PORT` | `3001` | Express server port |

## Architecture Quick Reference

```
Browser
  └── AnalyticsView.tsx
        └── api.getRecruiterAnalytics() / api.getDepartmentAnalytics()
              └── (mock mode) api-mock.ts → hardcoded data
              └── (real mode) api-real.ts → GET /api/stats/recruiters, /api/stats/departments
                    └── server/routes/stats.ts
                          └── StorageProvider.get() → aggregate from jobs + applications + users
```
