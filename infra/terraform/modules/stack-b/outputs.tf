output "app_url" {
  description = "Stack B public URL"
  value       = module.app_service.app_url
}

output "app_name" {
  description = "Stack B App Service name"
  value       = module.app_service.name
}

output "app_id" {
  description = "Stack B App Service resource ID"
  value       = module.app_service.id
}

output "default_hostname" {
  description = "Stack B default hostname"
  value       = module.app_service.default_hostname
}
