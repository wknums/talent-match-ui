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
