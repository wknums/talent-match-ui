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
  description = "Stable UUID for the delegated API scope"
  type        = string
  default     = "a3217e97-d4d2-4baa-b9f8-0e9ac6b05f31"
}

variable "api_scope_value" {
  description = "Delegated permission scope value"
  type        = string
  default     = "access_as_user"
}

variable "app_role_ids" {
  description = "Stable UUIDs keyed by persisted application role value"
  type        = map(string)
  default = {
    admin              = "52d6f322-c719-49a5-97a4-b7d78fc60af0"
    organization_admin = "fea2562e-d6f0-49b6-93a5-e666b51310f6"
    recruiter          = "edc0d4d0-bf46-4e88-b0e1-a2b465252a0c"
    business_panel     = "a98b2c62-70a4-41ab-95f4-960ea96ce06d"
  }

  validation {
    condition = length(setsubtract(
      toset(["admin", "organization_admin", "recruiter", "business_panel"]),
      toset(keys(var.app_role_ids))
    )) == 0
    error_message = "app_role_ids must define admin, organization_admin, recruiter, and business_panel."
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