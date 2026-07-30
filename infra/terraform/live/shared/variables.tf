# --- Core ---
variable "subscription_id" {
  description = "Azure subscription ID"
  type        = string
}

variable "tenant_id" {
  description = "Microsoft Entra tenant ID"
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

# --- Microsoft Entra applications ---
variable "reuse_entra" {
  description = "Reuse existing protected API and SPA applications"
  type        = bool
  default     = false
}

variable "existing_entra_api_app_client_id" {
  description = "Existing protected API application client ID"
  type        = string
  default     = ""
}

variable "existing_entra_api_service_principal_object_id" {
  description = "Existing protected API enterprise application object ID"
  type        = string
  default     = ""
}

variable "existing_entra_stack_a_client_id" {
  description = "Existing Stack A SPA application client ID"
  type        = string
  default     = ""
}

variable "existing_entra_stack_b_client_id" {
  description = "Existing Stack B SPA application client ID"
  type        = string
  default     = ""
}

variable "entra_api_identifier_uri" {
  description = "Unique identifier URI for the protected API"
  type        = string
}

variable "entra_api_scope_id" {
  description = "Stable UUID for the delegated protected-API scope"
  type        = string
  default     = "a3217e97-d4d2-4baa-b9f8-0e9ac6b05f31"
}

variable "entra_api_scope" {
  description = "Delegated protected-API scope value"
  type        = string
  default     = "access_as_user"
}

variable "entra_app_role_ids" {
  description = "Stable protected-API role UUIDs keyed by persisted role value"
  type        = map(string)
  default = {
    admin              = "52d6f322-c719-49a5-97a4-b7d78fc60af0"
    organization_admin = "fea2562e-d6f0-49b6-93a5-e666b51310f6"
    recruiter          = "edc0d4d0-bf46-4e88-b0e1-a2b465252a0c"
    business_panel     = "a98b2c62-70a4-41ab-95f4-960ea96ce06d"
  }
}

variable "entra_stack_a_redirect_uris" {
  description = "Allowed Stack A SPA redirect URIs"
  type        = list(string)
  default     = []
}

variable "entra_stack_b_redirect_uris" {
  description = "Allowed Stack B SPA redirect URIs"
  type        = list(string)
  default     = []
}

variable "entra_create_role_groups" {
  description = "Create baseline Entra security groups assigned to API roles"
  type        = bool
  default     = false
}

variable "entra_role_group_names" {
  description = "Security group display names keyed by protected-API role value"
  type        = map(string)
  default     = {}
}

variable "entra_bootstrap_admin_object_id" {
  description = "Entra user object ID assigned the protected API admin role"
  type        = string
  default     = ""
}

variable "awr_api_client_id" {
  description = "Existing AWReason API application client ID; empty disables managed-identity role assignments"
  type        = string
  default     = ""
}

variable "awr_api_app_role_value" {
  description = "AWReason application role assigned to the stack managed identities"
  type        = string
  default     = "TalentMatch.Access"
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
variable "enable_key_vault" {
  description = "Whether Key Vault is part of this deployment"
  type        = bool
  default     = false
}

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
variable "enable_apim" {
  description = "Whether API Management is part of this deployment"
  type        = bool
  default     = false
}

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

# --- Networking (US6) ---
variable "reuse_vnet" {
  description = "Must be true for VNet Integration (VNet creation not supported)"
  type        = bool
  default     = false
}

variable "vnet_name" {
  description = "Existing VNet name"
  type        = string
  default     = ""
}

variable "vnet_resource_group" {
  description = "Existing VNet resource group"
  type        = string
  default     = ""
}

variable "existing_integration_subnet_name" {
  description = "Existing delegated subnet name"
  type        = string
  default     = ""
}

variable "integration_subnet_cidr" {
  description = "CIDR for new delegated subnet"
  type        = string
  default     = ""
}

variable "reuse_sql_private_endpoint" {
  description = "Reuse an existing SQL private endpoint and DNS configuration"
  type        = bool
  default     = false
}

variable "sql_private_endpoint_subnet_name" {
  description = "Name for the dedicated SQL private endpoint subnet"
  type        = string
  default     = "snet-sql-private-endpoints"
}

variable "sql_private_endpoint_subnet_cidr" {
  description = "CIDR for the dedicated SQL private endpoint subnet"
  type        = string
  default     = ""
}

variable "allowed_ips" {
  description = "Allowed public IPs for App Service access"
  type        = list(string)
  default     = []
}
