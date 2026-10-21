output "id" {
  description = "Created or reused Application Insights ID, or empty when disabled"
  value       = local.id
}

output "connection_string" {
  description = "Created or reused Application Insights connection string, or empty when disabled"
  value       = local.connection_string
  sensitive   = true
}
