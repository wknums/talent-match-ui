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

variable "identity_client_id" {
  description = "User-assigned managed identity client ID for authenticated telemetry ingestion"
  type        = string
  default     = ""

  validation {
    condition     = var.app_insights_connection_string == "" || trimspace(var.identity_client_id) != ""
    error_message = "identity_client_id is required when Application Insights is enabled."
  }
}

variable "app_insights_connection_string" {
  description = "Application Insights connection string; empty disables App Service autoinstrumentation"
  type        = string
  default     = ""
  sensitive   = true
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

variable "app_command_line" {
  description = "Optional startup command for Linux App Service"
  type        = string
  default     = ""
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
