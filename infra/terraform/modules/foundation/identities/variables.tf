variable "reuse" {
  description = "Whether to look up existing identities instead of creating them"
  type        = bool
  default     = false
}

variable "existing_stack_a_name" {
  description = "Existing Stack A identity name (required when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_stack_b_name" {
  description = "Existing Stack B identity name (required when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_resource_group" {
  description = "Resource group of existing identities (required when reuse = true)"
  type        = string
  default     = ""
}

variable "stack_a_name" {
  description = "Stack A identity name (used when reuse = false)"
  type        = string
  default     = ""
}

variable "stack_b_name" {
  description = "Stack B identity name (used when reuse = false)"
  type        = string
  default     = ""
}

variable "location" {
  description = "Azure region"
  type        = string
  default     = ""
}

variable "resource_group_name" {
  description = "Resource group for managed identities"
  type        = string
  default     = ""
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
