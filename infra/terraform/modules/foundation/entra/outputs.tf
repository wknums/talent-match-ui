output "api_app_client_id" {
  description = "Protected API application client ID"
  value       = local.api_app_client_id
}

output "api_service_principal_object_id" {
  description = "Protected API enterprise application object ID"
  value       = local.api_service_principal_object_id
}

output "api_identifier_uri" {
  description = "Protected API identifier URI"
  value       = var.api_identifier_uri
}

output "api_scope" {
  description = "Delegated permission scope value"
  value       = var.api_scope_value
}

output "api_scope_id" {
  description = "Delegated permission scope UUID"
  value       = var.api_scope_id
}

output "stack_a_client_id" {
  description = "Stack A SPA application client ID"
  value       = local.stack_a_client_id
}

output "stack_b_client_id" {
  description = "Stack B SPA application client ID"
  value       = local.stack_b_client_id
}

output "app_role_ids" {
  description = "Stable API app-role UUIDs keyed by persisted role value"
  value       = var.app_role_ids
}

output "role_group_object_ids" {
  description = "Created security group object IDs keyed by application role value"
  value       = { for role, group in azuread_group.role : role => group.object_id }
}
output "source_connector_client_id" {
  description = "Client ID of the multi-tenant delegated Microsoft 365 source connector"
  value       = var.source_connector_enabled ? local.source_connector_client_id : ""
}

output "source_connector_federated_credential_ids" {
  description = "Federated credential IDs created on the source connector, keyed by identity name"
  value       = { for name, credential in azuread_application_federated_identity_credential.source_connector : name => credential.credential_id }
}
