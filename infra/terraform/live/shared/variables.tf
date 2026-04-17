# --- Core ---
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
  description = "Managed resource group name"
  type        = string
}

variable "project_name" {
  description = "Project name prefix for resources"
  type        = string
  default     = "talentmatch"
}

variable "stack_target" {
  description = "Deployment target (shared-only, stack-a, stack-b, both)"
  type        = string
  default     = "both"
}

# --- App Service Plan ---
variable "app_service_plan_sku" {
  description = "App Service plan SKU"
  type        = string
  default     = "B1"
}

variable "reuse_app_service_plan" {
  description = "Reuse an existing App Service plan"
  type        = bool
  default     = false
}

variable "existing_app_service_plan_name" {
  description = "Existing plan name (when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_app_service_plan_rg" {
  description = "Existing plan resource group (when reuse = true)"
  type        = string
  default     = ""
}

# --- SQL ---
variable "reuse_sql" {
  description = "Reuse an existing SQL server and database"
  type        = bool
  default     = false
}

variable "existing_sql_server_name" {
  description = "Existing SQL server name (when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_sql_database_name" {
  description = "Existing SQL database name (when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_sql_rg" {
  description = "Existing SQL resource group (when reuse = true)"
  type        = string
  default     = ""
}

variable "sql_admin_login" {
  description = "SQL admin login"
  type        = string
  default     = "sqladmin"
}

variable "sql_admin_password" {
  description = "SQL admin password"
  type        = string
  default     = ""
  sensitive   = true
}

variable "sql_aad_admin_login" {
  description = "Azure AD admin login for SQL"
  type        = string
  default     = ""
}

variable "sql_aad_admin_object_id" {
  description = "Azure AD admin object ID for SQL"
  type        = string
  default     = ""
}

# --- Key Vault ---
variable "reuse_key_vault" {
  description = "Reuse an existing Key Vault"
  type        = bool
  default     = false
}

variable "existing_key_vault_name" {
  description = "Existing Key Vault name (when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_key_vault_rg" {
  description = "Existing Key Vault resource group (when reuse = true)"
  type        = string
  default     = ""
}

# --- APIM ---
variable "reuse_apim" {
  description = "Reuse an existing API Management instance"
  type        = bool
  default     = false
}

variable "existing_apim_name" {
  description = "Existing APIM name (when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_apim_rg" {
  description = "Existing APIM resource group (when reuse = true)"
  type        = string
  default     = ""
}

# --- Identities ---
variable "reuse_identities" {
  description = "Reuse existing managed identities"
  type        = bool
  default     = false
}

variable "existing_identity_stack_a_name" {
  description = "Existing Stack A identity name (when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_identity_stack_b_name" {
  description = "Existing Stack B identity name (when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_identities_rg" {
  description = "Existing identities resource group (when reuse = true)"
  type        = string
  default     = ""
}
