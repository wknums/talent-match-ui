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

variable "stack_b_app_name_infix" {
  description = "App Service name segment between the project and environment for Stack B"
  type        = string
  default     = "blazor"
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

variable "app_insights_connection_string" {
  description = "Shared Application Insights connection string; empty disables monitoring"
  type        = string
  default     = ""
  sensitive   = true
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
  description = "Stack B SPA application client ID from shared root"
  type        = string
}

variable "entra_stack_a_client_id" {
  description = "Stack A SPA application client ID accepted by the shared protected API"
  type        = string
}

variable "entra_app_role_ids" {
  description = "Stable protected-API role UUIDs from shared root"
  type        = map(string)

  validation {
    condition = var.app_auth_mode != "entra" || length(setsubtract(
      toset(["admin", "organization_admin", "recruiter", "business_panel"]),
      toset(keys(var.entra_app_role_ids))
    )) == 0
    error_message = "entra_app_role_ids must define all four application roles."
  }
}

variable "entra_bootstrap_admin_object_id" {
  description = "First administrator Entra user object ID"
  type        = string
  default     = ""
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
  description = "AWReason sequential API endpoint passed to Stack B app settings"
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

variable "awr_scoring_lease_seconds" {
  description = "Seconds a document being scored stays reserved before it counts as abandoned (AWR_SCORING_LEASE_SECONDS); the host renews it every 20 seconds"
  type        = number
  default     = 240

  validation {
    condition     = var.awr_scoring_lease_seconds >= 60 && var.awr_scoring_lease_seconds <= 1800 && floor(var.awr_scoring_lease_seconds) == var.awr_scoring_lease_seconds
    error_message = "awr_scoring_lease_seconds must be a whole number from 60 to 1800."
  }
}

variable "cors_allowed_origins" {
  description = "Comma-separated browser origins allowed to make credentialed requests to Stack B"
  type        = string
  default     = ""
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

variable "bulk_auth_key_uri" {
  description = "Versionless URI of the Key Vault key that wraps bulk scoring authorization data keys (shared output bulk_auth_key_uri). Empty leaves BULK_AUTH_KEY_URI unset"
  type        = string
  default     = ""
}

variable "product_environment" {
  description = "Explicit product environment used by the publisher activation gate"
  type        = string
  default     = ""
}

variable "microsoft365_source_mode" {
  description = "Delegated connector mode; leave blank in checked-in roots and provide from an environment profile"
  type        = string
  default     = ""
}

variable "microsoft365_source_publisher_verified" {
  description = "Environment-owned publisher assertion; Production requires explicit true"
  type        = string
  default     = ""
}

variable "microsoft365_source_client_id" {
  description = "Delegated source connector client ID from the shared root"
  type        = string
  default     = ""
}

variable "microsoft365_source_callback_path" {
  description = "Local source authorization-code callback path"
  type        = string
  default     = ""
}

variable "microsoft365_source_admin_consent_callback_path" {
  description = "Local source admin-consent callback path"
  type        = string
  default     = ""
}

variable "microsoft365_source_authority" {
  description = "Commercial-cloud organizations authority supplied by an untracked environment profile"
  type        = string
  default     = ""
}

variable "microsoft365_source_scopes" {
  description = "Canonical delegated connector scopes"
  type        = string
  default     = ""
}

variable "microsoft_graph_base_uri" {
  description = "Commercial Microsoft Graph base URI supplied by an untracked environment profile"
  type        = string
  default     = ""
}

variable "microsoft365_source_max_concurrency" {
  description = "Maximum concurrent Microsoft Graph source calls per Stack B app instance"
  type        = string
  default     = ""
}

variable "bulk_source_idle_ttl_minutes" {
  description = "Pending source connection idle TTL"
  type        = string
  default     = ""
}

variable "bulk_source_absolute_ttl_minutes" {
  description = "Pending source connection absolute TTL"
  type        = string
  default     = ""
}

variable "bulk_discovery_step_files" {
  description = "Maximum eligible files committed by one discovery step"
  type        = string
  default     = ""
}

variable "bulk_discovery_step_max_entries" {
  description = "Maximum provider entries inspected by one discovery step"
  type        = string
  default     = ""
}

variable "bulk_discovery_step_max_seconds" {
  description = "Maximum duration of one discovery step"
  type        = string
  default     = ""
}

variable "bulk_max_total_bytes" {
  description = "Aggregate request byte limit"
  type        = string
  default     = ""
}

variable "bulk_pending_connections_per_actor" {
  description = "Maximum pending source connections per product actor"
  type        = string
  default     = ""
}

variable "bulk_connection_starts_per_actor_per_minute" {
  description = "Per-actor source connection start rate"
  type        = string
  default     = ""
}

variable "bulk_connection_starts_per_ip_per_minute" {
  description = "HMAC-subject source connection start rate"
  type        = string
  default     = ""
}

variable "bulk_callbacks_per_ip_per_minute" {
  description = "HMAC-subject source callback rate"
  type        = string
  default     = ""
}

variable "bulk_admission_hmac_key_uri" {
  description = "Key reference for admission-subject HMAC protection"
  type        = string
  default     = ""
}

variable "bulk_import_concurrency" {
  description = "Bounded import worker concurrency"
  type        = string
  default     = ""
}

variable "bulk_convert_concurrency" {
  description = "Bounded conversion worker concurrency"
  type        = string
  default     = ""
}

variable "bulk_raster_dpi" {
  description = "Configured raster fidelity DPI"
  type        = string
  default     = ""
}
