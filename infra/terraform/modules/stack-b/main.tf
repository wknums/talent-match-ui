# =============================================================================
# Stack B Composition Module
# =============================================================================
# Composes a .NET Blazor WASM App Service host for Stack B using the shared
# app-service building block and stable shared-root inputs.

module "app_service" {
  source = "../foundation/app-service"

  name                = "app-${var.project_name}-blazor-${var.environment}"
  location            = var.location
  resource_group_name = var.resource_group_name
  service_plan_id     = var.app_service_plan_id
  identity_id         = var.identity_id

  dotnet_version = "10.0"
  always_on      = var.environment != "dev"

  virtual_network_subnet_id = var.virtual_network_subnet_id
  allowed_ips               = var.allowed_ips

  app_settings = merge(
    {
      "DatabaseProvider"                     = "sqlserver"
      "KEY_VAULT_URI"                        = var.key_vault_uri
      "APIM_GATEWAY_URL"                    = var.apim_gateway_url
      "ConnectionStrings__DefaultConnection" = "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/sql-connection-string)"
      "AWR_API_KEY"                          = "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/awr-api-key)"
      "OPENAI_API_KEY"                       = "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/openai-api-key)"
    },
    var.extra_app_settings
  )

  tags = var.tags
}
