# =============================================================================
# Foundation Module: Networking — Outputs
# =============================================================================
# All outputs are stable regardless of whether the subnet was reused or created.

output "vnet_id" {
  description = "VNet resource ID"
  value       = data.azurerm_virtual_network.main.id
}

output "vnet_name" {
  description = "VNet name"
  value       = data.azurerm_virtual_network.main.name
}

output "integration_subnet_id" {
  description = "Integration subnet resource ID (stable regardless of reuse/create mode)"
  value       = var.existing_subnet_name != "" ? data.azurerm_subnet.existing[0].id : azurerm_subnet.integration[0].id
}

output "integration_subnet_name" {
  description = "Integration subnet name"
  value       = var.existing_subnet_name != "" ? data.azurerm_subnet.existing[0].name : azurerm_subnet.integration[0].name
}
