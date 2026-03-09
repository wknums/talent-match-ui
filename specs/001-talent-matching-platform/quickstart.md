# Developer Quickstart — Talent Matching Platform

## Prerequisites

- **Node.js** 22+ (for Stack A)
- **.NET 10 SDK** (for Stack B)
- **Git**

## Stack A — React / Express / TypeScript

### 1. Install dependencies

```bash
npm install
```

### 2. Configure environment

```bash
cp .env.example .env
# Edit .env:
#   API_MODE=mock   (for mock data, no server needed)
#   API_MODE=real   (for real KV-backed persistence)
#   AWRSEQAPI_ENDPOINT=https://your-api.azure.com  (for doc extraction / prompt generation)
```

### 3. Start the development server

```bash
npm run dev
```

Open `http://localhost:5000` in your browser.

### 4. Default credentials

- **Admin**: username=`admin`, password=`admin123`

### 5. Verify User Stories

| Story | How to verify |
|-------|---------------|
| US1 | Log in as admin → see all departments on dashboard |
| US2 | Open User Management → create a recruiter user |
| US3 | Click "Create Job" → fill in details with rubric |
| US3a | Open job detail → Prompt Management → create/generate/test/approve a prompt |
| US4 | Open a job → upload CV files (PDF/DOCX/MD) |
| US5 | After upload → check pipeline processing status |
| US6 | View job detail → check Longlist/Shortlist/Exclusions tabs |
| US7 | Click "Manual Review" on a flagged application |
| US8 | Check dashboard stats and pipeline visualiser |

## Stack B — .NET / Blazor WASM

### 1. Restore and build

```bash
cd dotnet
dotnet restore TalentMatch.slnx
dotnet build TalentMatch.slnx
```

### 2. Run the server

```bash
dotnet run --project src/Web.Server/TalentMatch.Web.Server.csproj
```

The API is at `https://localhost:5001`. Swagger UI at `/swagger`.

### 3. Run tests

```bash
dotnet test TalentMatch.slnx
```

### 4. Database

Stack B uses SQLite by default (file: `talentmatch.db`). EF Core migrations are applied automatically on startup.

To use SQL Server, update `appsettings.json`:
```json
{
  "DatabaseProvider": "sqlserver",
  "ConnectionStrings": {
    "DefaultConnection": "Server=.;Database=TalentMatch;Trusted_Connection=true;TrustServerCertificate=true"
  }
}
```

## Running Both Stacks

Both stacks can run simultaneously:
- Stack A: `npm run dev` → port 5000
- Stack B: `dotnet run --project dotnet/src/Web.Server/TalentMatch.Web.Server.csproj` → port 5001

Both implement the same API contracts and acceptance scenarios from `spec.md`.

## Prompt Management Workflow (US3a)

Both stacks support the full prompt lifecycle:

1. **Create Job** (US3) — job must have an approved rubric
2. **Create Prompt** — manual, import file, or generate from rubric via `AWRSEQAPI_ENDPOINT`
3. **Activate Prompt** — only one active prompt per job
4. **Test Prompt** — upload test applications, score them, review via manual review (US7)
5. **Approve for Production** — requires all test cases pass review without score changes
6. **Production Scoring** — blocked until a prompt is production-approved

### New API Endpoints (both stacks)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/jobs/:jobId/prompts` | List prompt revisions |
| POST | `/api/jobs/:jobId/prompts` | Create new prompt |
| PUT | `/api/jobs/:jobId/prompts/:promptId` | Edit (creates new revision) |
| POST | `/api/jobs/:jobId/prompts/:promptId/activate` | Activate prompt |
| POST | `/api/jobs/:jobId/prompts/:promptId/rate` | Rate and comment |
| POST | `/api/jobs/:jobId/prompts/generate` | AI-generate from rubric |
| POST | `/api/jobs/:jobId/prompts/:promptId/approve-production` | Approve for production |
| POST | `/api/jobs/:jobId/prompts/:promptId/test-runs` | Create test run |
| GET | `/api/jobs/:jobId/prompts/:promptId/test-runs` | List test runs |
| POST | `.../test-runs/:testRunId/approve` | Approve test run |

### Environment Variables

| Variable | Purpose | Required |
|----------|---------|----------|
| `AWRSEQAPI_ENDPOINT` | External API for document extraction and prompt generation | For US3/US3a AI features |
| `STORAGE_PROVIDER` | `local` or `azuresql` (Stack A) | Stack A only |
| `API_MODE` | `mock` or `real` (Stack A) | Stack A only |
| `DatabaseProvider` | `sqlite` or `sqlserver` (Stack B) | Stack B only |
