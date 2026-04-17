output "app_url" {
  description = "Stack B public URL"
  value       = module.stack_b.app_url
}

output "app_name" {
  description = "Stack B App Service name"
  value       = module.stack_b.app_name
}
