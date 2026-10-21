variable "enabled" {
  description = "Whether product Entra authentication is enabled"
  type        = bool
  default     = true
}

variable "enable_stack_a" {
  description = "Whether the environment includes the Stack A SPA"
  type        = bool
  default     = true
}

variable "enable_stack_b" {
  description = "Whether the environment includes the Stack B SPA"
  type        = bool
  default     = true
}

variable "reuse" {
  description = "Whether to reuse existing Entra applications and API service principal"
  type        = bool
  default     = false
}

variable "api_display_name" {
  description = "Display name for the protected API application"
  type        = string
}

variable "api_identifier_uri" {
  description = "Unique identifier URI for the protected API"
  type        = string
}

variable "api_scope_id" {
  description = "Explicit stable UUID for the delegated API scope; preserve the deployed value"
  type        = string
  default     = ""

  validation {
    condition     = !var.enabled || can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.api_scope_id))
    error_message = "api_scope_id must be an explicit UUID when product Entra authentication is enabled."
  }
}

variable "api_scope_value" {
  description = "Delegated permission scope value"
  type        = string
  default     = "access_as_user"
}

variable "app_role_ids" {
  description = "Explicit stable UUIDs keyed by persisted application role value; preserve deployed values"
  type        = map(string)
  default     = {}

  validation {
    condition = !var.enabled || (
      toset(keys(var.app_role_ids)) == toset(["admin", "organization_admin", "recruiter", "business_panel"])
      && alltrue([for id in values(var.app_role_ids) : can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", id))])
      && length(distinct([for id in values(var.app_role_ids) : lower(id)])) == 4
    )
    error_message = "app_role_ids must define four distinct explicit UUIDs for admin, organization_admin, recruiter, and business_panel when product Entra authentication is enabled."
  }
}

variable "stack_a_display_name" {
  description = "Display name for the Stack A SPA application"
  type        = string
}

variable "stack_a_redirect_uris" {
  description = "Allowed redirect URIs for the Stack A SPA"
  type        = list(string)
  default     = []
}

variable "stack_b_display_name" {
  description = "Display name for the Stack B SPA application"
  type        = string
}

variable "stack_b_redirect_uris" {
  description = "Allowed redirect URIs for the Stack B SPA"
  type        = list(string)
  default     = []
}

variable "existing_api_app_client_id" {
  description = "Existing protected API application client ID when reuse is true"
  type        = string
  default     = ""
}

variable "existing_api_service_principal_object_id" {
  description = "Existing protected API service principal object ID when reuse is true"
  type        = string
  default     = ""
}

variable "existing_stack_a_client_id" {
  description = "Existing Stack A SPA application client ID when reuse is true"
  type        = string
  default     = ""
}

variable "existing_stack_b_client_id" {
  description = "Existing Stack B SPA application client ID when reuse is true"
  type        = string
  default     = ""
}

variable "create_role_groups" {
  description = "Whether to create baseline security groups and assign them to API app roles"
  type        = bool
  default     = false
}

variable "role_group_names" {
  description = "Optional security group display names keyed by application role value"
  type        = map(string)
  default     = {}

  validation {
    condition = length(setsubtract(
      toset(keys(var.role_group_names)),
      toset(["admin", "organization_admin", "recruiter", "business_panel"])
    )) == 0
    error_message = "role_group_names keys must be supported application role values."
  }
}

variable "bootstrap_admin_object_id" {
  description = "Optional Entra user object ID assigned the protected API admin role"
  type        = string
  default     = ""
}
variable "source_connector_enabled" {
  description = "Provision or adopt the separate delegated Microsoft 365 authorization-code connector"
  type        = bool
  default     = false
}

variable "reuse_source_connector" {
  description = "Adopt an existing delegated source connector instead of creating it"
  type        = bool
  default     = false
}

variable "existing_source_connector_client_id" {
  description = "Existing source connector application client ID when reuse_source_connector is true"
  type        = string
  default     = ""
}

variable "source_connector_display_name" {
  description = "Display name of the delegated Microsoft 365 source connector"
  type        = string
  default     = "talent-microsoft365-source-connector"
}

variable "source_connector_redirect_uris" {
  description = "Exact Web callbacks accepted by the delegated source connector"
  type        = list(string)
  default     = []
}

variable "source_connector_federated_identity_principal_ids" {
  description = "Managed identity principal IDs allowed to authenticate the confidential source connector"
  type        = map(string)
  default     = {}
}
