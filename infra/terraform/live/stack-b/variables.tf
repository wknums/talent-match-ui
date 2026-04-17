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
