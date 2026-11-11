# Data Model — 007-fix-stackb-sql-endpoint

> This feature is a bugfix — no new entities, fields, or relationships are introduced.
> This document records the **existing** data structures affected by the two fixes.

## Affected Entity: Schema Bootstrap (DDL Execution)

The schema bootstrap reads `server/storage/schema.sql` and executes it as DDL batches against Azure SQL. The bug is in the *execution mechanism*, not the schema itself.

### Tables with Curly-Brace Defaults (cause of the `FormatException`)

These are the columns whose `DEFAULT '{}'` or `DEFAULT '[]'` values trigger the `ExecuteSqlRaw` format-string bug. All appear in sections 9+ of `schema.sql` — the portion that fails to execute:

| Table | Column | Default | Schema Line |
|-------|--------|---------|-------------|
| `ScoringResults` | `SubScoresJson` | `DEFAULT '{}'` | 166 |
| `ScoringResults` | `MustHaveResultJson` | `DEFAULT '{}'` | 167 |
| `ScoringResults` | `EvidenceCitationsJson` | `DEFAULT '[]'` | 168 |
| `AggregatedResults` | `FinalSubScoresJson` | `DEFAULT '{}'` | 191 |
| `PromptTestRuns` | `RubricScoresJson` | `DEFAULT '{}'` | 210 |
| `AuditEvents` | `DetailsJson` | `DEFAULT '{}'` | 287 |

### Impact

- **Before fix**: Schema bootstrap halts when it encounters the first `DEFAULT '{}'`. Tables defined after the failing batch are never created. Approximately 40% of tables are missing.
- **After fix**: All batches execute via `DbCommand.ExecuteNonQuery()`, which has no placeholder interpretation. All tables are created.

### Schema File Contract

- **Path**: `server/storage/schema.sql`
- **Modification**: NONE (FR-005). The file is read-only for this feature.
- **Splitting logic**: `Regex.Split` on `IF NOT EXISTS` and `CREATE INDEX` boundaries — unchanged.

## Affected Configuration: Terraform App Settings

### Stack B Live Root — Variable Addition

| Variable | Type | Default | Source |
|----------|------|---------|--------|
| `awr_seq_api_endpoint` | `string` | `""` | Mirrors Stack A `live/stack-a/variables.tf` line 88-92 |

### Stack B Live Root — Local Addition

```hcl
stack_b_extra_app_settings = var.awr_seq_api_endpoint != "" ? {
  AWR_SEQ_API_ENDPOINT = var.awr_seq_api_endpoint
} : {}
```

### Stack B Module — Pre-existing (no changes)

| Variable | Type | Default | Module Location |
|----------|------|---------|-----------------|
| `extra_app_settings` | `map(string)` | `{}` | `modules/stack-b/variables.tf` line 77 |

Merge occurs at `modules/stack-b/main.tf` line 23-36:
```hcl
app_settings = merge({ ... base settings ... }, var.extra_app_settings)
```

## State Transitions

N/A — This feature has no entity state transitions. The schema bootstrap is idempotent (guarded by `IF NOT EXISTS`), and the Terraform variable is additive.
