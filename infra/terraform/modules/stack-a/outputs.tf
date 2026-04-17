output "app_url" {
  description = "Stack A public URL"
  value       = module.app_service.app_url
}

output "app_name" {
  description = "Stack A App Service name"
  value       = module.app_service.name
}

output "app_id" {
  description = "Stack A App Service resource ID"
  value       = module.app_service.id
}

output "default_hostname" {
  description = "Stack A default hostname"
  value       = module.app_service.default_hostname
}
