variable "reuse" {
  description = "Whether to look up existing SQL resources instead of creating them"
  type        = bool
  default     = false
}

variable "existing_name" {
  description = "Existing SQL server name (required when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_database_name" {
  description = "Existing SQL database name (required when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_resource_group" {
  description = "Resource group of existing SQL resources (required when reuse = true)"
  type        = string
  default     = ""
}

variable "name" {
  description = "SQL server name (used when reuse = false)"
  type        = string
  default     = ""
}

variable "database_name" {
  description = "SQL database name (used when reuse = false)"
  type        = string
  default     = ""
}

variable "location" {
  description = "Azure region"
  type        = string
  default     = ""
}

variable "resource_group_name" {
  description = "Resource group for managed SQL"
  type        = string
  default     = ""
}

variable "admin_login" {
  description = "SQL admin login"
  type        = string
  default     = "sqladmin"
}

variable "admin_password" {
  description = "SQL admin password"
  type        = string
  default     = ""
  sensitive   = true
}

variable "aad_admin_login" {
  description = "Azure AD admin login username"
  type        = string
  default     = ""
}

variable "aad_admin_object_id" {
  description = "Azure AD admin object ID"
  type        = string
  default     = ""
}

variable "database_sku" {
  description = "SQL database SKU"
  type        = string
  default     = "Basic"
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
