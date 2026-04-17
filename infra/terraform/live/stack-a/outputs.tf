output "app_url" {
  description = "Stack A public URL"
  value       = module.stack_a.app_url
}

output "app_name" {
  description = "Stack A App Service name"
  value       = module.stack_a.app_name
}
