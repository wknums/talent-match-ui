variable "enabled" {
  description = "Whether Key Vault is part of this deployment"
  type        = bool
  default     = false
}

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

variable "bulk_auth_key_name" {
  description = "Name of the key that wraps bulk scoring authorization data keys. Empty means no key is created"
  type        = string
  default     = ""
}

variable "bulk_auth_crypto_user_principal_ids" {
  description = "Principal IDs given only wrap/unwrap (Key Vault Crypto User) on the bulk scoring key, keyed by a short name"
  type        = map(string)
  default     = {}
}
