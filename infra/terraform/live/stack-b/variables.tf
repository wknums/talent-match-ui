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

# --- Shared outputs consumed by this root ---
variable "app_service_plan_id" {
  description = "Shared App Service plan ID from shared root"
  type        = string
}

variable "identity_id" {
  description = "Stack B managed identity ID from shared root"
  type        = string
}

variable "identity_client_id" {
  description = "Stack B managed identity client ID from shared root"
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

variable "openai_api_key" {
  description = "Direct OpenAI API key value for non-sensitive environments"
  type        = string
  default     = ""
  sensitive   = true
}

variable "awr_seq_api_endpoint" {
  description = "AWReason sequential API endpoint passed to Stack B app settings"
  type        = string
  default     = ""
}

variable "awr_auth_mode" {
  description = "AWReason auth mode passed to app settings"
  type        = string
  default     = "none"
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
