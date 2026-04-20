# Implementation Plan: US6 — Private Network Connectivity

**Branch**: `006-selective-azure-deploy` | **Date**: 2026-04-18 | **Spec**: `specs/006-selective-azure-deploy/spec.md` (User Story 6)
**Input**: Feature specification US6 (FR-023 through FR-032, SC-013 through SC-018)
**Scope**: Incremental plan — US1–US4 (T001–T038) and US5 (T039–T054) are fully implemented.

## Summary

User Story 6 adds private network connectivity to the existing selective Azure deployment infrastructure. Both App Services (Stack A and Stack B) must gain VNet Integration via a delegated subnet on an existing VNet to communicate with Azure SQL over a Private Endpoint, and IP access restrictions to allow only explicitly specified public IPs (deny all else). The existing VNet and SQL Private Endpoint are reused — not created. This is a Terraform-only change; no application code modifications are required since Private DNS resolution transparently routes SQL connections over the private network.

**Technical approach**: A new `modules/foundation/networking/` Terraform module follows the established reuse pattern to look up an existing VNet, conditionally reuse or create a delegated integration subnet, and output the subnet ID. The existing `modules/foundation/app-service/` module gains `virtual_network_subnet_id` and `ip_restriction` support. The deploy scripts and `.env` example files are extended with new networking variables.

## Technical Context

**Language/Version**: HCL (Terraform ≥1.9), Bash 5  
**Primary Dependencies**: azurerm provider ≥4.x, Azure App Service, Azure Virtual Network  
**Storage**: N/A (no schema or data changes)  
**Testing**: `terraform validate`, `terraform plan` (dry run), manual connectivity verification  
**Target Platform**: Azure (australiaeast region)  
**Project Type**: Infrastructure-as-code (Terraform modules + Bash wrappers)  
**Performance Goals**: N/A (infrastructure provisioning, not runtime)  
**Constraints**: Existing VNet must be reused (no VNet creation); SQL Private Endpoint already exists (skip creation); minimum subnet CIDR is /26 for App Service delegation  
**Scale/Scope**: 2 App Services, 1 shared VNet, 1 shared subnet, IP restrictions per App Service

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Typed & Auditable | ✅ PASS | No application entities affected — infrastructure-only changes |
| II. Layered Architecture | ✅ PASS | No application code changes |
| III. Storage Abstraction | ✅ PASS | Connection strings unchanged; Private DNS resolves transparently |
| IV. Security Defaults | ✅ PASS | IP restrictions enforce deny-all posture; no secrets exposed |
| V. LLM Integration | ✅ PASS | Not affected |
| VI. UI Precision | ✅ PASS | Not affected |
| VII. Simplicity & YAGNI | ✅ PASS | Only adds what spec requires; reuse pattern follows existing convention |
| IX. Clean Architecture | ✅ PASS | No .NET code changes |
| Technology Stacks | ✅ PASS | Uses Terraform + Bash per constitution mandate |

**Gate result: PASS** — No violations. US6 is purely infrastructure; it does not touch application code, types, storage abstractions, or UI.

## Project Structure

### Documentation (this feature)

```text
specs/006-selective-azure-deploy/
├── plan.md              # This file (US6 incremental plan)
├── research.md          # Phase 0 output (appended with R-012..R-015)
├── data-model.md        # Phase 1 output (appended with entities 17..20)
├── quickstart.md        # Phase 1 output (appended with networking verification)
├── contracts/           # Phase 1 output (updated with networking contracts)
└── tasks.md             # Phase 2 output (T055+ tasks generated separately)
```

### Source Code (repository root)

```text
infra/terraform/
├── modules/
│   ├── foundation/
│   │   ├── networking/          # NEW — VNet data lookup + subnet reuse/create
│   │   │   ├── main.tf
│   │   │   ├── variables.tf
│   │   │   └── outputs.tf
│   │   └── app-service/         # MODIFIED — gains VNet Integration + IP restrictions
│   │       ├── main.tf
│   │       ├── variables.tf
│   │       └── outputs.tf
│   ├── stack-a/                 # MODIFIED — passes networking inputs to app-service
│   │   ├── main.tf
│   │   └── variables.tf
│   └── stack-b/                 # MODIFIED — passes networking inputs to app-service
│       ├── main.tf
│       └── variables.tf
├── live/
│   ├── shared/                  # MODIFIED — calls networking module, outputs subnet_id
│   │   ├── main.tf
│   │   ├── variables.tf
│   │   └── outputs.tf
│   ├── stack-a/                 # MODIFIED — accepts + passes networking inputs
│   │   ├── main.tf
│   │   └── variables.tf
│   └── stack-b/                 # MODIFIED — accepts + passes networking inputs
│       ├── main.tf
│       └── variables.tf
└── ...

infra/scripts/
├── lib/common.sh                # MODIFIED — new TF_VAR exports + validation
└── deploy.sh                    # MODIFIED — passes subnet_id to stack roots

.env_local.example               # MODIFIED — new networking variables
.env_qa.example                  # MODIFIED — new networking variables
.env_prod.example                # MODIFIED — new networking variables
```

**Structure Decision**: Follows the established pattern — a new foundation module in `modules/foundation/networking/` is called by `live/shared/` (since the VNet/subnet is shared by both stacks per FR-028). The subnet ID flows as an output from shared root → stack root variables → stack composition module → foundation app-service module, mirroring how `app_service_plan_id` and `identity_id` flow today.

## Complexity Tracking

No violations to justify — all changes follow existing patterns.
