# Contract: Schema Qualification Interface (US5)

**Feature**: 006-selective-azure-deploy — User Story 5
**Date**: 2026-04-18

> Defines the schema qualification contracts for both stacks and the migration script interface.

## Stack A — Table Name Helper

**Module**: `server/storage/table-names.ts`

### Exported API

```typescript
/**
 * Returns a schema-qualified table name for Azure SQL, or an unqualified
 * name for SQLite. The schema is a fixed constant ("talentmatch").
 *
 * @param tableName - PascalCase table name (e.g., "Users", "Applications")
 * @returns "[talentmatch].[Users]" for Azure SQL, "Users" for SQLite
 */
export function T(tableName: string): string
```

### Behavior Contract

| Condition | Input | Output |
|-----------|-------|--------|
| `STORAGE_PROVIDER=azuresql` with Azure SQL metadata configured | `T('Users')` | `[talentmatch].[Users]` |
| Azure SQL metadata is unset / local SQLite path | `T('Users')` | `Users` |

### Constants

| Constant | Value | Configurable |
|----------|-------|-------------|
| `SCHEMA` | `'talentmatch'` | No — fixed by spec assumption |

### Dependencies

| Import | From |
|--------|------|
| `isAzureSql` | `./db.js` |

### Consuming Modules

All 5 repository files MUST import and use `T()` for every table reference in SQL queries:

| Module | Approximate Call Count |
|--------|----------------------|
| `server/storage/repos/application-repo.ts` | ~43 |
| `server/storage/repos/job-repo.ts` | ~14 |
| `server/storage/repos/audit-repo.ts` | ~9 |
| `server/storage/repos/prompt-repo.ts` | ~12 |
| `server/storage/repos/user-repo.ts` | ~10 |

### Usage Pattern

```typescript
import { T } from '../table-names.js'

// In query construction
await pool.request()
  .input('id', sql.NVarChar, id)
  .query(`SELECT * FROM ${T('Users')} WHERE Id = @id`)

// In JOIN queries
.query(`SELECT a.*, j.Title
        FROM ${T('Applications')} a
        JOIN ${T('Jobs')} j ON a.JobId = j.Id
        WHERE a.Id = @id`)

// In INSERT queries
.query(`INSERT INTO ${T('ProcessingEvents')} (Id, Actor, Action, ...)
        VALUES (@id, @actor, @action, ...)`)
```

---

## Stack B — EF Core Default Schema

**File**: `dotnet/src/Infrastructure/Persistence/AppDbContext.cs`

### Contract

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    // Schema qualification: only for SQL Server (SQLite has no schema concept)
    if (Database.IsSqlServer())
        modelBuilder.HasDefaultSchema("talentmatch");

    // ... existing entity configurations unchanged
}
```

### Behavior Contract

| Provider | Schema Applied | EF-Generated SQL Pattern |
|----------|---------------|-------------------------|
| SQL Server | `talentmatch` | `SELECT ... FROM [talentmatch].[Users] ...` |
| SQLite | None (default) | `SELECT ... FROM "Users" ...` |

### Impact on Migrations

- Existing migrations target `dbo` schema (no explicit schema in migration code)
- New migrations generated after this change will target `talentmatch` schema
- The migration script (`migrate-schema-dbo-to-talentmatch.sql`) bridges existing databases

---

## Schema DDL Contract (schema.sql)

**File**: `server/storage/schema.sql`

### Preamble (new)

```sql
-- Ensure talentmatch schema exists
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch')
    EXEC('CREATE SCHEMA [talentmatch]');
```

### Table Definition Pattern (updated)

```sql
-- Before
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
CREATE TABLE Users ( ... );

-- After
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users' AND schema_id = SCHEMA_ID('talentmatch'))
CREATE TABLE [talentmatch].[Users] ( ... );
```

### Index Definition Pattern (updated)

```sql
-- Before
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Users_Username')
    CREATE UNIQUE INDEX UX_Users_Username ON Users (Username);

-- After
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Users_Username')
    CREATE UNIQUE INDEX UX_Users_Username ON [talentmatch].[Users] (Username);
```

---

## Migration Script Contract

**File**: `infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql`

### Interface

- **Input**: Execute against any Azure SQL database that may have tables in `dbo`
- **Output**: All 15 application tables transferred to `talentmatch` schema
- **Idempotent**: Yes — skips tables already in target schema
- **Data loss**: None — `ALTER SCHEMA TRANSFER` is metadata-only
- **Transactional**: Per-table (each transfer is atomic)

### Execution

```bash
# Via Azure CLI
az sql db query --server <server> --name <db> --resource-group <rg> \
  --query "$(cat infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql)"

# Via sqlcmd
sqlcmd -S <server>.database.windows.net -d <db> -G \
  -i infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql
```

### Table Transfer Order

All 15 tables in creation order (order is not significant for `ALTER SCHEMA TRANSFER`):

1. Users
2. PasswordResetRequests
3. Jobs
4. JobConfigVersions
5. Applications
6. ApplicationDocuments
7. DocumentBlobs
8. ExtractionArtifacts
9. ScoringRuns
10. AggregatedResults
11. ManualReviews
12. ScoringPrompts
13. PromptTestRuns
14. FailureQueueItems
15. ProcessingEvents

---

## Application Startup Schema Initialization

**File**: `server/storage/db.ts` — `initializeDatabase()` function

### Updated Contract

For Azure SQL path, before executing `schema.sql`, ensure the schema exists:

```typescript
if (isAzureSql) {
    // Ensure talentmatch schema exists (FR-021)
    await pool.request().query(`
        IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch')
            EXEC('CREATE SCHEMA [talentmatch]');
    `)
    // Then run schema.sql as before
}
```

This provides defense-in-depth: even if the Terraform provisioner hasn't run yet, the application will create the schema on startup.
