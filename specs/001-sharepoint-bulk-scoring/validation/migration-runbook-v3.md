# Stack B Delegated Source Migration Runbook

**Feature**: `001-sharepoint-bulk-scoring`  
**Applies to**: Stack B SQLite and Azure SQL providers  
**Status**: Activated for the delegated-v3 Stack B migration; concrete environment values remain
in an untracked operator profile.

## Safety contract

- Take a provider-native backup and place the application in a maintenance/drain window.
- Never translate an old reader, application token, OBO cache, source allow-list, credential,
  certificate, or site grant into delegated-v3 state.
- Never record protected payloads, source URLs/names, account/email values, tenant/resource IDs,
  tokens, fingerprints, or content in operator evidence.
- Completed history is retained and verified by count and opaque request identifiers.
- `Down` is schema-only. It cannot restore discarded authorization material, deleted staged
  content, or any removed reader/application-only path.

## 1. Inventory before schema change

Run the application-owned `BulkSourceMigrationPreflight.InventoryAsync` against the target provider
from the authorized maintenance shell. Capture only:

- active request count;
- draft request count;
- active authorization count;
- completed-history request count;
- provider kind, migration name, UTC time, and sanitized command result.

Do not proceed while `IsDrained` is false. `RequireDrainedAsync` must fail with
`delegated_v3_old_active_work_requires_drain_or_discard`; that refusal is the expected safe result.

## 2. Drain or cancel old work

1. Stop new source-connection and confirmation traffic.
2. Let active imports reach a terminal state where practical.
3. Use the existing authorized cancellation operation for work that cannot drain. Wait for worker
   fencing and terminal settlement; do not update request/item rows by hand.
4. Abandon expired drafts through the product cleanup path.
5. Rerun inventory. A request is not considered drained merely because its credential was removed.

## 3. Discard protected material

After a named operator confirms the irreversible action, run
`DiscardOldAuthorizationMaterialAsync(operatorConfirmed: true)`.

The operation:

- clears payload and wrapped-key bytes through the domain discard transition;
- commits each discard with `request_authorization_discarded`;
- uses service actor `bulk-source-migration-service`, UTC time, an opaque request subject, a fresh
  correlation ID, and safe reason code `migration_protected_material_discarded`;
- rolls back the discard if its audit insert fails.

The false-confirmation call must be rejected. Rerun `RequireDrainedAsync`; discarded credentials do
not waive remaining active/draft work.

## 4. Apply provider migration

With inventory drained and a backup available, apply the repository EF Core migrations using the
environment-owned connection configuration:

```powershell
dotnet ef database update --project dotnet/src/Infrastructure --startup-project dotnet/src/Web.Server
```

For SQLite, operate on a copied database and atomically replace it only after validation. For Azure
SQL, apply under the approved change window and retain the pre-change restore point. Do not place a
connection string or environment identifier in checked-in evidence.

## 5. Verify completed history and new schema

1. Run `BulkSourceMigrationPreflight.VerifyCompletedHistoryAsync` with the captured pre-migration
   completed-history count.
2. Run:

   ```powershell
   dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkSourceOperationsTests"
   dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~BulkSourceConnectionMigrationTests"
   ```

3. Verify old active authorization material is absent; completed requests/items remain readable;
   delegated connection/request bindings, checks, indexes, cascades, and detached audit retention
   match on SQLite and Azure SQL.
4. Resume Stack B only after the delegated connector settings and publisher gate are valid.

## 6. Rollback and recovery

An EF `Down` operation may revert schema only. It does **not** and must not recreate discarded
credentials, old access-model selectors, site grants, passwords/certificates, staged content, or
removed application permissions. If operational recovery needs pre-change protected state, stop
the application and restore the provider-native backup into an isolated recovery environment;
never synthesize credentials or copy them into the delegated schema.

Record the UTC decision, provider, migration boundary, sanitized counts, test exits, and restore
point reference. Never record the restore point's concrete resource name in this repository.
