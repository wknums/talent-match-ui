# Research — 007-fix-stackb-sql-endpoint

## R1: Why `ExecuteSqlRaw` Fails on Curly Braces

**Decision**: Replace `db.Database.ExecuteSqlRaw(batch)` with ADO.NET `DbCommand.ExecuteNonQuery()`.

**Rationale**: EF Core's `ExecuteSqlRaw(string sql)` passes the SQL string through `String.Format`-style processing. When SQL contains `DEFAULT '{}'`, the `{}` is interpreted as a format placeholder (`{0}` with index 0), causing a `FormatException` because no corresponding parameter is provided. This is by-design behavior documented in the [EF Core raw SQL docs](https://learn.microsoft.com/en-us/ef/core/querying/sql-queries#passing-parameters): `ExecuteSqlRaw` treats `{n}` tokens as parameter placeholders. There is no flag to disable this — the API fundamentally conflates format tokens with SQL content.

ADO.NET's `DbCommand.ExecuteNonQuery()` has no such interpretation. The command text is sent to SQL Server verbatim, which is exactly what DDL bootstrap requires.

**Alternatives considered**:
1. **Escape curly braces in SQL** (`'{{}}'` instead of `'{}'`) — Rejected: violates FR-005 (no schema SQL changes) and would make the SQL non-standard and fragile.
2. **Use `ExecuteSqlInterpolated`** — Rejected: same placeholder interpretation issue; designed for parameterized queries, not raw DDL.
3. **Use `db.Database.ExecuteSql(FormattableString)`** — Rejected: also interprets interpolation holes; cannot pass raw DDL.
4. **Prepend `SET NOEXEC OFF` or use `sp_executesql`** — Rejected: over-engineering; adds complexity for no benefit over direct `DbCommand`.

**Project precedent**: The SQLite bootstrap (`EnsureSharedSqliteSchemaIfNeeded`, Program.cs lines 287-322) already uses `connection.CreateCommand()` + `ExecuteNonQuery()` for the identical purpose. The ADO.NET fix brings Azure SQL bootstrap into alignment with the existing SQLite pattern.

---

## R2: How to Obtain the ADO.NET Connection from EF Core DbContext

**Decision**: Use `db.Database.GetDbConnection()` (returns the underlying `DbConnection`), then `connection.CreateCommand()`.

**Rationale**: EF Core exposes the underlying ADO.NET connection via `DatabaseFacade.GetDbConnection()`. This is the same method used by the SQLite bootstrap at line 292:
```csharp
var connection = (SqliteConnection)db.Database.GetDbConnection();
```

For Azure SQL, the cast is `SqlConnection` (from `Microsoft.Data.SqlClient`), but since `DbCommand.ExecuteNonQuery()` is a base `DbCommand` method, no cast is needed if we use the generic `DbConnection.CreateCommand()` API.

**Connection lifecycle**: The method must open the connection if not already open and close it afterward (matching the SQLite pattern's `shouldClose` guard at lines 227-229, 318-321). This preserves FR-003 (same connection-management approach).

---

## R3: Stack B Terraform — `extra_app_settings` Wiring Pattern

**Decision**: Mirror Stack A's conditional local pattern in Stack B's live root.

**Rationale**: Stack A's `live/stack-a/main.tf` (lines 14-16) uses:
```hcl
stack_a_extra_app_settings = var.awr_seq_api_endpoint != "" ? {
  AWR_SEQ_API_ENDPOINT = var.awr_seq_api_endpoint
} : {}
```
This conditional ensures the app setting is only added when a non-empty value is provided (FR-007). The Stack B module already accepts `extra_app_settings` (module variable at line 77) and merges it at line 35 of `modules/stack-b/main.tf`:
```hcl
app_settings = merge({ ... }, var.extra_app_settings)
```

The only missing pieces are:
1. `variables.tf` in `live/stack-b/` needs the `awr_seq_api_endpoint` variable declaration (mirroring Stack A)
2. `main.tf` in `live/stack-b/` needs the conditional local and `extra_app_settings = local.stack_b_extra_app_settings` argument

**Alternatives considered**:
1. **Hard-code `AWR_SEQ_API_ENDPOINT` in the module's base app_settings** — Rejected: violates FR-007 (conditional inclusion) and diverges from Stack A's pattern.
2. **Pass the raw variable to the module and let the module do conditional logic** — Rejected: the module already has a clean `extra_app_settings` merge; adding variable-specific conditional logic to the shared module would break separation of concerns.

---

## R4: Deployment Pipeline — `common.sh` and `.env_qa` Verification

**Decision**: No changes needed to `common.sh` or `.env_qa` (FR-009 confirmed).

**Rationale**:
- `infra/scripts/lib/common.sh` line 132 already exports: `export TF_VAR_awr_seq_api_endpoint="${AWR_SEQ_API_ENDPOINT:-}"`
- The `.env_qa` file (and `.env_qa.example`) already defines `AWR_SEQ_API_ENDPOINT` with the correct endpoint value.
- The Terraform variable naming convention (`TF_VAR_awr_seq_api_endpoint`) matches the new variable name in Stack B's `variables.tf`.

Once the Terraform variable is declared in Stack B's live root, `terraform plan` / `terraform apply` will automatically pick up the value from the existing environment export.

---

## R5: Post-Fix Verification Strategy

**Decision**: Use the existing toolchain for rebuild, deploy, and verify.

**Steps** (documented in `quickstart.md`):
1. **Rebuild**: `infra/scripts/package-stack-b.sh` — produces `artifacts/stack-b/` with the updated `Program.cs`
2. **Deploy artifact**: `az webapp deploy` against the Stack B App Service
3. **Apply Terraform**: `terraform apply` in `infra/terraform/live/stack-b/` — adds the `AWR_SEQ_API_ENDPOINT` app setting
4. **Verify schema**: Query Azure SQL for table count matching schema file table count
5. **Verify AWR connectivity**: Hit the Stack B health endpoint and confirm `awrApi` is not `"skipped"`
