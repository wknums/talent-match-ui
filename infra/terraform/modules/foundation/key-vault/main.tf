# =============================================================================
# Foundation Module: Key Vault (reuse-aware)
# =============================================================================

data "azurerm_client_config" "current" {}

# --- Data source for reused Key Vault ---
data "azurerm_key_vault" "existing" {
  count               = var.enabled && var.reuse ? 1 : 0
  name                = var.existing_name
  resource_group_name = var.existing_resource_group
}

# --- Managed Key Vault ---
resource "azurerm_key_vault" "main" {
  count                         = var.enabled && !var.reuse ? 1 : 0
  name                          = var.name
  location                      = var.location
  resource_group_name           = var.resource_group_name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  public_network_access_enabled = false
  soft_delete_retention_days    = 7
  purge_protection_enabled      = false

  tags = var.tags
}

# --- Bulk scoring: key that wraps each request's authorization data key ---
# RSA, wrap and unwrap only. The key never leaves the vault. Each wrapped data key records the key version that wrapped
# it, so rotation is safe: new versions wrap new data, older versions stay readable until their records are discarded
# (an authorization lives at most BULK_AUTH_MAX_AGE_HOURS, 72 by default). The key is created through the ARM control
# plane so the Terraform runner does not need direct access to the private-only Key Vault data-plane endpoint.
locals {
  vault_id      = var.enabled ? (var.reuse ? data.azurerm_key_vault.existing[0].id : azurerm_key_vault.main[0].id) : ""
  vault_uri     = var.enabled ? (var.reuse ? data.azurerm_key_vault.existing[0].vault_uri : azurerm_key_vault.main[0].vault_uri) : ""
  bulk_auth_key = var.enabled && var.bulk_auth_key_name != ""
}

resource "azapi_resource" "bulk_auth" {
  count = local.bulk_auth_key ? 1 : 0

  type      = "Microsoft.KeyVault/vaults/keys@2023-07-01"
  name      = var.bulk_auth_key_name
  parent_id = local.vault_id
  body = {
    properties = {
      kty     = "RSA"
      keySize = 3072
      keyOps  = ["wrapKey", "unwrapKey"]
      rotationPolicy = {
        attributes = {
          expiryTime = "P2Y"
        }
        lifetimeActions = [
          {
            action = {
              type = "rotate"
            }
            trigger = {
              timeAfterCreate = "P90D"
            }
          },
          {
            action = {
              type = "notify"
            }
            trigger = {
              timeBeforeExpiry = "P30D"
            }
          },
        ]
      }
    }
  }

  tags = var.tags
}

# The API hosts' managed identities may only wrap and unwrap with this one key: no secrets, no other keys, no export.
resource "azurerm_role_assignment" "bulk_auth_crypto_user" {
  for_each = local.bulk_auth_key ? var.bulk_auth_crypto_user_principal_ids : {}

  scope                = azapi_resource.bulk_auth[0].id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = each.value
  principal_type       = "ServicePrincipal"
}
