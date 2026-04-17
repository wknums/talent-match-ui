variable "reuse" {
  description = "Whether to look up an existing App Service plan instead of creating one"
  type        = bool
  default     = false
}

variable "existing_name" {
  description = "Name of the existing App Service plan (required when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_resource_group" {
  description = "Resource group of the existing App Service plan (required when reuse = true)"
  type        = string
  default     = ""
}

variable "name" {
  description = "Name for the new App Service plan (used when reuse = false)"
  type        = string
  default     = ""
}

variable "location" {
  description = "Azure region"
  type        = string
  default     = ""
}

variable "resource_group_name" {
  description = "Resource group for the new plan"
  type        = string
  default     = ""
}

variable "sku_name" {
  description = "App Service plan SKU (e.g., B1, B2, S1, P1V3)"
  type        = string
  default     = "B1"
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
