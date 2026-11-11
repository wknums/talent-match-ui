output "id" {
  description = "App Service plan resource ID"
  value       = var.reuse ? data.azurerm_service_plan.existing[0].id : azurerm_service_plan.main[0].id
}

output "name" {
  description = "App Service plan name"
  value       = var.reuse ? data.azurerm_service_plan.existing[0].name : azurerm_service_plan.main[0].name
}

output "kind" {
  description = "App Service plan kind"
  value       = var.reuse ? data.azurerm_service_plan.existing[0].kind : azurerm_service_plan.main[0].kind
}
