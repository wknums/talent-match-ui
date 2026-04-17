variable "reuse" {
  description = "Whether to look up an existing Key Vault instead of creating one"
  type        = bool
  default     = false
}

variable "existing_name" {
  description = "Existing Key Vault name (required when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_resource_group" {
  description = "Resource group of existing Key Vault (required when reuse = true)"
  type        = string
  default     = ""
}

variable "name" {
  description = "Key Vault name (used when reuse = false)"
  type        = string
  default     = ""
}

variable "location" {
  description = "Azure region"
  type        = string
  default     = ""
}

variable "resource_group_name" {
  description = "Resource group for managed Key Vault"
  type        = string
  default     = ""
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
