variable "name" {
  description = "App Service name"
  type        = string
}

variable "location" {
  description = "Azure region"
  type        = string
}

variable "resource_group_name" {
  description = "Resource group"
  type        = string
}

variable "service_plan_id" {
  description = "App Service plan ID"
  type        = string
}

variable "identity_id" {
  description = "User-assigned managed identity resource ID"
  type        = string
}

variable "always_on" {
  description = "Keep the app always on"
  type        = bool
  default     = false
}

variable "node_version" {
  description = "Node.js version (e.g., '20-lts'). Set empty for non-Node apps."
  type        = string
  default     = ""
}

variable "dotnet_version" {
  description = ".NET version (e.g., '10.0'). Set empty for non-.NET apps."
  type        = string
  default     = ""
}

variable "app_settings" {
  description = "Application settings key-value pairs"
  type        = map(string)
  default     = {}
}

variable "virtual_network_subnet_id" {
  description = "Integration subnet ID for VNet Integration. When set, enables outbound VNet connectivity."
  type        = string
  default     = null
}

variable "allowed_ips" {
  description = "Public IPs to allow — when non-empty, deny-all default is enforced"
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
