# Talent Matching Platform

AI-powered CV/resume screening and scoring platform with configurable rubrics, multi-run scoring, and manual review workflow.

## Prerequisites

- **Node.js** 20.19+ or 22.12+
- **npm** 10+

## Quick Start

```bash
# Install dependencies
npm install

# Start both backend server and frontend dev server
npm run dev
```

This runs two processes concurrently:
- **Backend API** on `http://localhost:3001`
- **Vite frontend** on `http://localhost:5173`

Open `http://localhost:5173` in your browser. Default login: `admin` / `adm1n99`

## Available Scripts

| Script | Description |
|---|---|
| `npm run dev` | Start backend + frontend together |
| `npm run dev:client` | Start only the Vite frontend |
| `npm run dev:server` | Start only the Express backend |
| `npm run build` | Build frontend for production |
| `npm run build:server` | Compile backend TypeScript |
| `npm start` | Run compiled backend (after `build:server`) |
| `npm run lint` | Run ESLint |

## Storage Configuration

The backend supports two database drivers:

### SQLite (default — local development)

No configuration needed. Data is stored in `shared-data/talentmatch.db`. Tables are created without schema qualification.

### Azure SQL

```env
AZURE_SQL_CONNECTION_STRING=Server=your-server.database.windows.net;Database=your-db;User Id=your-user;Password=your-password;Encrypt=true
```

Requires `mssql` package (included as optional dependency). The server will run `schema.sql` automatically on first connection.

Azure SQL pay-as-you-go databases can take 30-90 seconds to wake after idle periods. Both stacks now include startup resilience for that behavior:

- **Node.js (Stack A)** retries initial `mssql` connection attempts with bounded exponential backoff.
- **.NET (Stack B)** applies SQL Server provider transient retries, a minimum 90 second connect timeout, and retries startup migration/seed work.
- **Operations guidance**: do not set a lower SQL connect timeout than 90 seconds in Azure-hosted connection strings unless you are prepared to absorb cold-start failures.

Stack A retry tuning can be overridden with environment variables:

```env
AZURE_SQL_WAKEUP_MAX_ATTEMPTS=8
AZURE_SQL_WAKEUP_INITIAL_DELAY_MS=2000
AZURE_SQL_WAKEUP_MAX_DELAY_MS=15000
```

When cold-start retries occur, the service logs retry scheduling, eventual success-after-retry, and retry-budget exhaustion to help diagnose database wake-up delays.

**Schema isolation**: Azure SQL deployments use the `talentmatch` schema — all 15 application tables are created under `[talentmatch].[TableName]` instead of the default `dbo` schema. This provides namespace isolation in shared database environments.

- **Node.js (Stack A)**: Uses the `T()` helper from `server/storage/table-names.ts` to qualify table names. When adding a new table, use `T('NewTable')` in your SQL queries — schema qualification is automatic.
- **\.NET (Stack B)**: Uses `HasDefaultSchema("talentmatch")` in `AppDbContext.cs`, applied only on SQL Server.
- **Local SQLite**: Completely unaffected. The `T()` helper returns plain table names, and `HasDefaultSchema` is skipped.

## LLM Configuration (Optional)

Document extraction features (uploading job specs and rubrics) require an LLM API key. Configure in `.env`:

### OpenAI

```env
OPENAI_API_KEY=sk-...
```

### Azure OpenAI

```env
AZURE_OPENAI_API_KEY=your-key
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com
```

Without an LLM key configured, the app works fully - document extraction will show an error but manual entry of jobs/rubrics works.

## Environment Variables

Copy `.env.example` to `.env` and configure:

| Variable | Default | Description |
|---|---|---|
| `PORT` | `3001` | Backend server port |
| `AZURE_SQL_CONNECTION_STRING` | - | Set to use Azure SQL instead of SQLite |
| `AZURE_SQL_WAKEUP_MAX_ATTEMPTS` | `8` | Stack A Azure SQL startup retry budget |
| `AZURE_SQL_WAKEUP_INITIAL_DELAY_MS` | `2000` | Stack A initial Azure SQL retry delay |
| `AZURE_SQL_WAKEUP_MAX_DELAY_MS` | `15000` | Stack A cap for Azure SQL retry backoff |
| `OPENAI_API_KEY` | - | OpenAI API key (option 1) |
| `AZURE_OPENAI_API_KEY` | - | Azure OpenAI key (option 2) |
| `AZURE_OPENAI_ENDPOINT` | - | Azure OpenAI endpoint URL |

## Project Structure

```
server/                      # Express backend
  index.ts                   # Server entry point
  routes/
    llm.ts                   # LLM proxy endpoint
  storage/
    db.ts                    # Dual-driver database layer (Azure SQL / SQLite)
    schema.sql               # Azure SQL DDL (talentmatch schema)
    schema-sqlite.sql        # SQLite DDL
    table-names.ts           # T() schema-qualification helper
    repos/                   # Data access repositories
src/                         # React frontend
  lib/
    spark-client.ts          # KV + LLM client (calls backend API)
    api.ts                   # Application API layer (mock data + KV)
    auth.ts                  # Authentication (KV-backed)
  components/                # UI components
  types/                     # TypeScript type definitions
.env.example                 # Environment variable template
vite.config.ts               # Vite config with API proxy
```

---

## Stack B — .NET / Blazor WASM

The project includes a parallel .NET implementation using Clean Architecture in `dotnet/`.

### Prerequisites

- .NET 10 SDK or later
- Node.js 22+ (for Stack A)

### Running Stack B

```bash
cd dotnet
dotnet restore TalentMatch.slnx
dotnet build TalentMatch.slnx
dotnet run --project src/Web.Server/TalentMatch.Web.Server.csproj
```

The API will be available at `https://localhost:5001` with Swagger UI at `/swagger`.

### Running Tests

```bash
# Stack A tests
npm test

# Stack B tests
cd dotnet
dotnet test TalentMatch.slnx
```

### Project Structure (Stack B)

```
dotnet/
├── src/
│   ├── Domain/           # Core domain entities and interfaces
│   ├── Application/      # MediatR handlers, validators, DTOs
│   ├── Infrastructure/   # EF Core, repositories, services
│   ├── Web.Server/       # ASP.NET Core API endpoints
│   └── Web.Client/       # Blazor WASM frontend
└── tests/
    ├── Domain.Tests/
    ├── Application.Tests/
    ├── Infrastructure.Tests/
    └── Web.Tests/
```

### Configuration

Stack B uses `appsettings.json` for configuration:
- `DatabaseProvider`: `sqlite` (default) or `sqlserver`
- `ConnectionStrings:DefaultConnection`: Database connection string
- `AzureOpenAI:Endpoint`: Azure OpenAI endpoint (for LLM features)

When `DatabaseProvider=sqlserver`, Stack B enforces Azure SQL resilience defaults in code:
- SQL connect timeout is raised to at least 90 seconds.
- EF Core enables transient retries with up to 6 retries and a 15 second max retry delay.
- Startup migration and seeding are retried with bounded exponential backoff for transient wake-up failures.

In local development, both stacks now default to the same shared SQLite file at `shared-data/talentmatch.db` under the repo root.
You can override that location for either stack with `SQLITE_DB_PATH`.

### API Mode

Stack A supports `API_MODE=mock|real` in `.env` to switch between mock and real API implementations.
