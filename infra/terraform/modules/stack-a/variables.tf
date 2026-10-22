variable "environment" {
  description = "Deployment environment (dev, test, prod)"
  type        = string
}

variable "location" {
  description = "Azure region"
  type        = string
}

variable "resource_group_name" {
  description = "Resource group name"
  type        = string
}

variable "project_name" {
  description = "Project name prefix"
  type        = string
  default     = "talentmatch"
}

variable "app_name_infix" {
  description = "App Service name segment between the project and environment"
  type        = string
  default     = "node"

  validation {
    condition     = can(regex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", var.app_name_infix))
    error_message = "app_name_infix must contain lowercase letters, digits or internal hyphens, starting and ending with a letter or digit."
  }

  validation {
    condition     = length("app-${var.project_name}-${var.app_name_infix}-${var.environment}") <= 60
    error_message = "The generated Stack A App Service name must not exceed 60 characters."
  }
}

variable "app_service_plan_id" {
  description = "Shared App Service plan ID"
  type        = string
}

variable "identity_id" {
  description = "Stack A managed identity ID"
  type        = string
}

variable "identity_client_id" {
  description = "Stack A managed identity client ID"
  type        = string
}

variable "key_vault_uri" {
  description = "Key Vault URI for secret references"
  type        = string
}

variable "sql_server_fqdn" {
  description = "SQL Server FQDN"
  type        = string
}

variable "sql_database_name" {
  description = "SQL database name"
  type        = string
}

variable "apim_gateway_url" {
  description = "APIM gateway URL"
  type        = string
}

variable "app_insights_connection_string" {
  description = "Shared Application Insights connection string; empty disables monitoring"
  type        = string
  default     = ""
  sensitive   = true
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

variable "extra_app_settings" {
  description = "Additional app settings to merge"
  type        = map(string)
  default     = {}
}

variable "virtual_network_subnet_id" {
  description = "Integration subnet ID for VNet Integration"
  type        = string
  default     = null
}

variable "allowed_ips" {
  description = "Public IPs to allow — when non-empty, deny-all default is enforced"
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
