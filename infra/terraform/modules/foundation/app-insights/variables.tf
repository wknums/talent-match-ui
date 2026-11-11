variable "enabled" {
  description = "Enable shared Application Insights monitoring"
  type        = bool
  default     = false
}

variable "reuse" {
  description = "Look up an existing Application Insights component instead of creating monitoring resources"
  type        = bool
  default     = false
}

variable "existing_name" {
  description = "Existing Application Insights component name"
  type        = string
  default     = ""

  validation {
    condition     = !var.enabled || !var.reuse || trimspace(var.existing_name) != ""
    error_message = "existing_name is required when Application Insights is enabled and reused."
  }
}

variable "existing_resource_group" {
  description = "Resource group containing the reused component, which may differ from the application's group"
  type        = string
  default     = ""

  validation {
    condition     = !var.enabled || !var.reuse || trimspace(var.existing_resource_group) != ""
    error_message = "existing_resource_group is required when Application Insights is enabled and reused."
  }
}

variable "name" {
  description = "Name of the managed Application Insights component"
  type        = string
}

variable "workspace_name" {
  description = "Name of the managed Log Analytics workspace"
  type        = string
}

variable "location" {
  description = "Region for managed monitoring resources"
  type        = string
}

variable "resource_group_name" {
  description = "Resource group for managed monitoring resources"
  type        = string
}

variable "publisher_principal_ids" {
  description = "Managed identity principal IDs keyed by enabled stack"
  type        = map(string)
  default     = {}
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
