variable "enabled" {
  description = "Whether API Management is part of this deployment"
  type        = bool
  default     = false
}

variable "reuse" {
  description = "Whether to look up an existing APIM instance instead of creating one"
  type        = bool
  default     = false
}

variable "existing_name" {
  description = "Existing APIM name (required when reuse = true)"
  type        = string
  default     = ""
}

variable "existing_resource_group" {
  description = "Resource group of existing APIM (required when reuse = true)"
  type        = string
  default     = ""
}

variable "name" {
  description = "APIM name (used when reuse = false)"
  type        = string
  default     = ""
}

variable "location" {
  description = "Azure region"
  type        = string
  default     = ""
}

variable "resource_group_name" {
  description = "Resource group for managed APIM"
  type        = string
  default     = ""
}

variable "publisher_name" {
  description = "APIM publisher name"
  type        = string
  default     = "TalentMatch"
}

variable "publisher_email" {
  description = "APIM publisher email"
  type        = string
  default     = "admin@example.com"
}

variable "sku_name" {
  description = "APIM SKU (e.g., Consumption_0, Developer_1, Basic_1)"
  type        = string
  default     = "Consumption_0"
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
