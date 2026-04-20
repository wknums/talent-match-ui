variable "environment" {
  description = "Deployment environment (dev, test, prod)"
  type        = string
}

variable "location" {
  description = "Azure region"
  type        = string
}

variable "resource_group_name" {
  description = "Resource group name"
  type        = string
}

variable "project_name" {
  description = "Project name prefix"
  type        = string
  default     = "talentmatch"
}

variable "app_service_plan_id" {
  description = "Shared App Service plan ID"
  type        = string
}

variable "identity_id" {
  description = "Stack A managed identity ID"
  type        = string
}

variable "key_vault_uri" {
  description = "Key Vault URI for secret references"
  type        = string
}

variable "sql_server_fqdn" {
  description = "SQL Server FQDN"
  type        = string
}

variable "sql_database_name" {
  description = "SQL database name"
  type        = string
}

variable "apim_gateway_url" {
  description = "APIM gateway URL"
  type        = string
}

variable "extra_app_settings" {
  description = "Additional app settings to merge"
  type        = map(string)
  default     = {}
}

variable "virtual_network_subnet_id" {
  description = "Integration subnet ID for VNet Integration"
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
