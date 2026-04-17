output "server_id" {
  description = "SQL Server resource ID"
  value       = var.reuse ? data.azurerm_mssql_server.existing[0].id : azurerm_mssql_server.main[0].id
}

output "server_name" {
  description = "SQL Server name"
  value       = var.reuse ? data.azurerm_mssql_server.existing[0].name : azurerm_mssql_server.main[0].name
}

output "server_fqdn" {
  description = "SQL Server fully qualified domain name"
  value       = var.reuse ? data.azurerm_mssql_server.existing[0].fully_qualified_domain_name : azurerm_mssql_server.main[0].fully_qualified_domain_name
}

output "database_id" {
  description = "SQL Database resource ID"
  value       = var.reuse ? data.azurerm_mssql_database.existing[0].id : azurerm_mssql_database.main[0].id
}

output "database_name" {
  description = "SQL Database name"
  value       = var.reuse ? data.azurerm_mssql_database.existing[0].name : azurerm_mssql_database.main[0].name
}
