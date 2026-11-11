output "id" {
  description = "App Service resource ID"
  value       = azurerm_linux_web_app.main.id
}

output "name" {
  description = "App Service name"
  value       = azurerm_linux_web_app.main.name
}

output "default_hostname" {
  description = "App Service default hostname"
  value       = azurerm_linux_web_app.main.default_hostname
}

output "app_url" {
  description = "App Service public URL"
  value       = "https://${azurerm_linux_web_app.main.default_hostname}"
}
