# =============================================================================
# Live Root: Stack A (Node.js/Express)
# =============================================================================
# Deploys Stack A app host consuming shared outputs. Does not manage Stack B.

locals {
  tags = {
    environment = var.environment
    project     = var.project_name
    stack       = "stack-a"
    managedBy   = "terraform"
  }
}

module "stack_a" {
  source = "../../modules/stack-a"

  environment         = var.environment
  location            = var.location
  resource_group_name = var.resource_group_name
  project_name        = var.project_name
  app_service_plan_id = var.app_service_plan_id
  identity_id         = var.identity_id
  key_vault_uri       = var.key_vault_uri
  sql_server_fqdn     = var.sql_server_fqdn
  sql_database_name   = var.sql_database_name
  apim_gateway_url    = var.apim_gateway_url

  virtual_network_subnet_id = var.integration_subnet_id != "" ? var.integration_subnet_id : null
  allowed_ips               = var.allowed_ips

  tags = local.tags
}
