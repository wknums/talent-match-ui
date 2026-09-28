# =============================================================================
# Shared Root Outputs
# =============================================================================
# All outputs are stable regardless of whether resources are created or reused.

output "resource_group_name" {
  description = "Managed resource group name"
  value       = azurerm_resource_group.main.name
}

output "entra_api_app_client_id" {
  description = "Protected API application client ID"
  value       = module.entra.api_app_client_id
}

output "entra_api_service_principal_object_id" {
  description = "Protected API enterprise application object ID"
  value       = module.entra.api_service_principal_object_id
}

output "entra_api_identifier_uri" {
  description = "Protected API identifier URI"
  value       = module.entra.api_identifier_uri
}

output "entra_api_scope" {
  description = "Delegated protected-API scope value"
  value       = module.entra.api_scope
}

output "entra_api_scope_id" {
  description = "Delegated protected-API scope UUID"
  value       = module.entra.api_scope_id
}

output "entra_stack_a_client_id" {
  description = "Stack A SPA application client ID"
  value       = module.entra.stack_a_client_id
}

output "entra_stack_b_client_id" {
  description = "Stack B SPA application client ID"
  value       = module.entra.stack_b_client_id
}

output "entra_app_role_ids" {
  description = "Stable protected-API app-role UUIDs"
  value       = module.entra.app_role_ids
}

output "entra_role_group_object_ids" {
  description = "Created role-group object IDs"
  value       = module.entra.role_group_object_ids
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

output "integration_subnet_id" {
  description = "Delegated subnet ID for App Service VNet Integration"
  value       = var.reuse_vnet ? module.networking[0].integration_subnet_id : ""
}

output "sql_private_endpoint_id" {
  description = "Created SQL private endpoint ID, or empty when reusing one"
  value       = var.reuse_vnet ? module.networking[0].sql_private_endpoint_id : ""
}
