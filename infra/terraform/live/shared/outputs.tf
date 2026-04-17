# =============================================================================
# Shared Root Outputs
# =============================================================================
# All outputs are stable regardless of whether resources are created or reused.

output "resource_group_name" {
  description = "Managed resource group name"
  value       = azurerm_resource_group.main.name
}

output "app_service_plan_id" {
  description = "Shared App Service plan ID"
  value       = module.app_service_plan.id
}

output "sql_server_fqdn" {
  description = "SQL Server fully qualified domain name"
  value       = module.sql.server_fqdn
}

output "sql_database_name" {
  description = "SQL database name"
  value       = module.sql.database_name
}

output "key_vault_uri" {
  description = "Key Vault URI"
  value       = module.key_vault.vault_uri
}

output "key_vault_name" {
  description = "Key Vault name"
  value       = module.key_vault.name
}

output "apim_gateway_url" {
  description = "API Management gateway URL"
  value       = module.apim.gateway_url
}

output "identity_stack_a_id" {
  description = "Stack A managed identity resource ID"
  value       = module.identities.stack_a_id
}

output "identity_stack_b_id" {
  description = "Stack B managed identity resource ID"
  value       = module.identities.stack_b_id
}

output "identity_stack_a_principal_id" {
  description = "Stack A managed identity principal ID"
  value       = module.identities.stack_a_principal_id
}

output "identity_stack_b_principal_id" {
  description = "Stack B managed identity principal ID"
  value       = module.identities.stack_b_principal_id
}

output "identity_stack_a_client_id" {
  description = "Stack A managed identity client ID"
  value       = module.identities.stack_a_client_id
}

output "identity_stack_b_client_id" {
  description = "Stack B managed identity client ID"
  value       = module.identities.stack_b_client_id
}

output "sql_server_id" {
  description = "SQL Server resource ID"
  value       = module.sql.server_id
}

output "sql_server_name" {
  description = "SQL Server name"
  value       = module.sql.server_name
}
