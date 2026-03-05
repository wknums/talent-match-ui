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

The backend supports two storage providers, controlled by the `STORAGE_PROVIDER` environment variable in `.env`:

### Local KV (default)

```env
STORAGE_PROVIDER=local
```

Data is persisted to `.data/kv-store.json` on disk. No external dependencies required.

### Azure SQL

```env
STORAGE_PROVIDER=azuresql
AZURE_SQL_CONNECTION_STRING=Server=your-server.database.windows.net;Database=your-db;User Id=your-user;Password=your-password;Encrypt=true
```

Requires `mssql` package (included as optional dependency). The server will create a `kv_store` table automatically on first run.

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
| `STORAGE_PROVIDER` | `local` | `local` or `azuresql` |
| `PORT` | `3001` | Backend server port |
| `AZURE_SQL_CONNECTION_STRING` | - | Required when `STORAGE_PROVIDER=azuresql` |
| `OPENAI_API_KEY` | - | OpenAI API key (option 1) |
| `AZURE_OPENAI_API_KEY` | - | Azure OpenAI key (option 2) |
| `AZURE_OPENAI_ENDPOINT` | - | Azure OpenAI endpoint URL |

## Project Structure

```
server/                      # Express backend
  index.ts                   # Server entry point
  routes/
    kv.ts                    # KV storage REST API
    llm.ts                   # LLM proxy endpoint
  storage/
    types.ts                 # StorageProvider interface
    factory.ts               # Provider factory (reads STORAGE_PROVIDER)
    local-kv.ts              # File-backed KV store
    azure-sql.ts             # Azure SQL KV store
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

### API Mode

Stack A supports `API_MODE=mock|real` in `.env` to switch between mock and real API implementations.
