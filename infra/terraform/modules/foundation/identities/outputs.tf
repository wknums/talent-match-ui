output "stack_a_id" {
  description = "Stack A managed identity resource ID"
  value       = var.reuse ? data.azurerm_user_assigned_identity.stack_a[0].id : azurerm_user_assigned_identity.stack_a[0].id
}

output "stack_a_principal_id" {
  description = "Stack A managed identity principal ID"
  value       = var.reuse ? data.azurerm_user_assigned_identity.stack_a[0].principal_id : azurerm_user_assigned_identity.stack_a[0].principal_id
}

output "stack_a_client_id" {
  description = "Stack A managed identity client ID"
  value       = var.reuse ? data.azurerm_user_assigned_identity.stack_a[0].client_id : azurerm_user_assigned_identity.stack_a[0].client_id
}

output "stack_b_id" {
  description = "Stack B managed identity resource ID"
  value       = var.reuse ? data.azurerm_user_assigned_identity.stack_b[0].id : azurerm_user_assigned_identity.stack_b[0].id
}

output "stack_b_principal_id" {
  description = "Stack B managed identity principal ID"
  value       = var.reuse ? data.azurerm_user_assigned_identity.stack_b[0].principal_id : azurerm_user_assigned_identity.stack_b[0].principal_id
}

output "stack_b_client_id" {
  description = "Stack B managed identity client ID"
  value       = var.reuse ? data.azurerm_user_assigned_identity.stack_b[0].client_id : azurerm_user_assigned_identity.stack_b[0].client_id
}
