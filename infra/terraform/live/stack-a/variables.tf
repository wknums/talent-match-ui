variable "subscription_id" {
  description = "Azure subscription ID"
  type        = string
}

variable "environment" {
  description = "Deployment environment (dev, test, prod)"
  type        = string
  validation {
    condition     = contains(["dev", "test", "prod"], var.environment)
    error_message = "Environment must be dev, test, or prod."
  }
}

variable "location" {
  description = "Azure region"
  type        = string
  default     = "australiaeast"
}

variable "resource_group_name" {
  description = "Resource group name"
  type        = string
}

variable "project_name" {
  description = "Project name prefix for resources"
  type        = string
  default     = "talentmatch"
}

variable "tenant_id" {
  description = "Microsoft Entra tenant ID from the authoritative environment profile"
  type        = string
}

# --- Shared outputs consumed by this root ---
variable "app_service_plan_id" {
  description = "Shared App Service plan ID from shared root"
  type        = string
}

variable "identity_id" {
  description = "Stack A managed identity ID from shared root"
  type        = string
}

variable "identity_client_id" {
  description = "Stack A managed identity client ID from shared root"
  type        = string
}

variable "key_vault_uri" {
  description = "Key Vault URI from shared root"
  type        = string
}

variable "sql_server_fqdn" {
  description = "SQL Server FQDN from shared root"
  type        = string
}

variable "sql_database_name" {
  description = "SQL database name from shared root"
  type        = string
}

variable "apim_gateway_url" {
  description = "APIM gateway URL from shared root"
  type        = string
}

variable "app_auth_mode" {
  description = "Application authentication mode"
  type        = string
  default     = "entra"

  validation {
    condition     = contains(["simple", "entra"], var.app_auth_mode)
    error_message = "app_auth_mode must be simple or entra."
  }
}

variable "entra_api_app_client_id" {
  description = "Protected API application client ID from shared root"
  type        = string
}

variable "entra_api_service_principal_object_id" {
  description = "Protected API enterprise application object ID from shared root"
  type        = string
}

variable "entra_api_identifier_uri" {
  description = "Protected API audience/identifier URI from shared root"
  type        = string
}

variable "entra_api_scope" {
  description = "Delegated protected-API scope value from shared root"
  type        = string
}

variable "entra_spa_client_id" {
  description = "Stack A SPA application client ID from shared root"
  type        = string
}

variable "entra_app_role_ids" {
  description = "Stable protected-API role UUIDs from shared root"
  type        = map(string)

  validation {
    condition = length(setsubtract(
      toset(["admin", "organization_admin", "recruiter", "business_panel"]),
      toset(keys(var.entra_app_role_ids))
    )) == 0
    error_message = "entra_app_role_ids must define all four application roles."
  }
}

variable "use_key_vault_secret_refs" {
  description = "When true, app settings use Key Vault references for runtime secrets"
  type        = bool
  default     = true
}

variable "awr_api_key" {
  description = "Direct AWR API key value for non-sensitive environments"
  type        = string
  default     = ""
  sensitive   = true
}

variable "awr_seq_api_endpoint" {
  description = "AWReason sequential API endpoint passed to Stack A app settings"
  type        = string
  default     = ""
}

variable "awr_auth_mode" {
  description = "AWReason auth mode passed to app settings"
  type        = string
  default     = "none"
}

variable "awr_aad_audience" {
  description = "AWReason API token scope used by managed-identity authentication"
  type        = string
  default     = ""
}

variable "awr_max_parallel" {
  description = "Maximum parallel AWR operations allowed at runtime"
  type        = number
  default     = 1
}

variable "api_mode" {
  description = "Application API mode"
  type        = string
  default     = "mock"
}

# --- Networking (US6) ---
variable "integration_subnet_id" {
  description = "Delegated subnet ID for VNet Integration from shared root"
  type        = string
  default     = ""
}

variable "allowed_ips" {
  description = "Allowed public IPs for IP restrictions"
  type        = list(string)
  default     = []
}
