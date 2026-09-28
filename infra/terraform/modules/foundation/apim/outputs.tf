output "id" {
  description = "APIM resource ID"
  value       = var.enabled ? (var.reuse ? data.azurerm_api_management.existing[0].id : azurerm_api_management.main[0].id) : ""
}

output "name" {
  description = "APIM name"
  value       = var.enabled ? (var.reuse ? data.azurerm_api_management.existing[0].name : azurerm_api_management.main[0].name) : ""
}

output "gateway_url" {
  description = "APIM gateway URL"
  value       = var.enabled ? (var.reuse ? data.azurerm_api_management.existing[0].gateway_url : azurerm_api_management.main[0].gateway_url) : ""
}
