# Infrastructure: Selective Azure Deployment

This directory contains all deployment automation for the TalentMatch platform, supporting selective deployment of Stack A (Node.js/Express), Stack B (.NET Blazor WASM), or both to Azure.

## Architecture

### Three Terraform Live Roots

The infrastructure uses three independent Terraform state roots to enable selective deployment and safe lifecycle management:

```text
infra/terraform/live/
├── shared/     # Shared infrastructure (Resource Group, App Service Plan, SQL, Key Vault, APIM, Identities)
├── stack-a/    # Stack A App Service (Node.js/Express)
└── stack-b/    # Stack B App Service (.NET Blazor WASM)
```

- **Shared root** provisions or reuses shared Azure resources consumed by both stacks.
- **Stack A root** deploys only the Node.js/Express app host.
- **Stack B root** deploys only the .NET Blazor WASM app host.

### Reuse-Aware Foundation Modules

Each shared resource module supports a **create-or-reuse** pattern:

```text
infra/terraform/modules/foundation/
├── app-service-plan/   # Shared compute plan
├── sql/                # Azure SQL Server + Database
├── key-vault/          # Key Vault for secrets
├── apim/               # API Management gateway
├── identities/         # User-assigned managed identities
└── app-service/        # App Service building block (always created)
```

When `reuse=true`, the module uses a `data` source to look up the existing resource. When `reuse=false`, it creates the resource. **Outputs remain stable** regardless of ownership.

### Stack Composition Modules

```text
infra/terraform/modules/
├── stack-a/   # Node.js/Express app composition
└── stack-b/   # .NET Blazor WASM app composition
```

## Environment Profiles

| Profile | Terraform Env | GitHub Env | Usage |
|---------|---------------|------------|-------|
| `.env_local` | `dev` | `development` | Local development and testing |
| `.env_qa` | `test` | `staging` | QA/staging on Azure |
| `.env_prod` | `prod` | `production` | Production on Azure |

Start from the tracked examples:

```bash
cp .env_local.example .env_local
cp .env_qa.example .env_qa
cp .env_prod.example .env_prod
```

## Deployment Commands

### Local / CLI Deployment

```bash
# Deploy shared + Stack A to dev
./infra/scripts/deploy.sh .env_local dev apply stack-a

# Deploy shared + Stack B to dev
./infra/scripts/deploy.sh .env_local dev apply stack-b

# Deploy both stacks to QA
./infra/scripts/deploy.sh .env_qa test apply both

# Plan shared-only changes in production
./infra/scripts/deploy.sh .env_prod prod plan shared-only
```

### Usage Pattern

```bash
./infra/scripts/deploy.sh <env-file> <tf-environment> <action> <target>
```

| Argument | Options |
|----------|---------|
| `env-file` | `.env_local`, `.env_qa`, `.env_prod` |
| `tf-environment` | `dev`, `test`, `prod` |
| `action` | `plan`, `apply` |
| `target` | `shared-only`, `stack-a`, `stack-b`, `both` |

## GitHub Actions Workflow

The workflow at `.github/workflows/selective-azure-deploy.yml` supports:

- **Push triggers**: Automatically detects changed scopes and deploys only affected stacks.
- **Manual dispatch**: Choose environment, target, and action explicitly.

### Changed-Path Detection

| Scope | Paths |
|-------|-------|
| Shared | `infra/**`, `.github/workflows/**`, `.env_*.example` |
| Stack A | `src/**`, `server/**`, `package.json`, `vite.config.ts`, `tsconfig*.json`, `tailwind.config.js` |
| Stack B | `dotnet/**` |

### Workflow Jobs

1. **resolve-targets** — Determines which scopes to deploy
2. **plan-or-apply-shared** — Applies shared infrastructure first
3. **package-stack-a** — Builds and packages Stack A (parallel with package-stack-b)
4. **package-stack-b** — Publishes and packages Stack B (parallel with package-stack-a)
5. **deploy-stack-a** — Deploys Stack A (after shared + package)
6. **deploy-stack-b** — Deploys Stack B (after shared + package)

## Safe Deprovisioning

**Always use the deprovision wrapper** — never run raw `terraform destroy`.

```bash
# Preview what would be destroyed
./infra/scripts/deprovision.sh .env_local dev stack-a --dry-run

# Destroy Stack A only
./infra/scripts/deprovision.sh .env_local dev stack-a

# Destroy Stack B only (with force for CI)
./infra/scripts/deprovision.sh .env_qa test stack-b --force

# Destroy shared infrastructure (requires separate confirmation)
./infra/scripts/deprovision.sh .env_prod prod shared
```

### Reuse Protection

Resources with `*_REUSE=TRUE` in the `.env_*` file are:
- Never created as Terraform-managed resources (only `data` sources)
- Marked as `[PROTECTED]` in the deprovision summary
- Never destroyed, even with `--force`

### Deprovision Order

1. Stack-specific resources first (`stack-a`, `stack-b`, or `both`)
2. Shared infrastructure separately (requires dedicated `shared` target)

## Reuse Configuration

To reuse an existing resource, set the reuse flag and provide coordinates in your `.env_*` file:

```ini
AZ_SQL_REUSE=TRUE
SQL_SERVER_NAME=sql-shared-qa
SQL_DATABASE_NAME=awrdb
SQL_RG=rg-shared-platform
```

The deploying identity needs at least `Reader` access on external resource groups containing reused resources.

## Scripts Reference

| Script | Purpose |
|--------|---------|
| `infra/scripts/deploy.sh` | Main deployment wrapper |
| `infra/scripts/deprovision.sh` | Safe destroy with reuse protection |
| `infra/scripts/resolve-targets.sh` | Workflow target resolution |
| `infra/scripts/package-stack-a.sh` | Build and package Stack A |
| `infra/scripts/package-stack-b.sh` | Publish and package Stack B |
| `infra/scripts/lib/common.sh` | Shared Bash helpers |
