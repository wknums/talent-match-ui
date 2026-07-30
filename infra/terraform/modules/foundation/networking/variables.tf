# =============================================================================
# Foundation Module: Networking — Variables
# =============================================================================
# Looks up an existing VNet and either reuses or creates a delegated subnet
# for App Service VNet Integration.

variable "vnet_name" {
  description = "Existing VNet name"
  type        = string

  validation {
    condition     = length(var.vnet_name) > 0
    error_message = "vnet_name is required for VNet data source lookup"
  }
}

variable "vnet_resource_group" {
  description = "Resource group containing the VNet"
  type        = string

  validation {
    condition     = length(var.vnet_resource_group) > 0
    error_message = "vnet_resource_group is required for VNet data source lookup"
  }
}

variable "existing_subnet_name" {
  description = "Existing delegated subnet name (mutually exclusive with subnet_cidr)"
  type        = string
  default     = ""
}

variable "subnet_cidr" {
  description = "CIDR for new delegated subnet (mutually exclusive with existing_subnet_name)"
  type        = string
  default     = ""
}

variable "subnet_name" {
  description = "Name for newly created subnet (used only when creating)"
  type        = string
  default     = "snet-appservice-integration"
}

variable "reuse_sql_private_endpoint" {
  description = "Reuse an existing SQL private endpoint and DNS configuration"
  type        = bool
  default     = false
}

variable "sql_private_endpoint_subnet_name" {
  description = "Name for the dedicated SQL private endpoint subnet"
  type        = string
  default     = "snet-sql-private-endpoints"
}

variable "sql_private_endpoint_subnet_cidr" {
  description = "CIDR for the dedicated SQL private endpoint subnet"
  type        = string
  default     = ""
}

variable "sql_server_id" {
  description = "Azure SQL logical server resource ID"
  type        = string
  default     = ""
}

variable "sql_server_name" {
  description = "Azure SQL logical server name"
  type        = string
  default     = ""
}

variable "tags" {
  description = "Resource tags"
  type        = map(string)
  default     = {}
}
