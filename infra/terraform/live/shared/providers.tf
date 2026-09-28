provider "azuread" {
  tenant_id = var.tenant_id
}

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy = true
    }
  }

  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id
}
