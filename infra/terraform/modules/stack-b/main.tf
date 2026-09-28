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

  dotnet_version    = "10.0"
  always_on         = var.environment != "dev"
  app_command_line = "dotnet TalentMatch.Web.Server.dll"

  virtual_network_subnet_id = var.virtual_network_subnet_id
  allowed_ips               = var.allowed_ips

  app_settings = merge(
    {
      "DatabaseProvider"              = "sqlserver"
      "AZURE_SQL_AUTH_MODE"           = "entra"
      "AZURE_SQL_SERVER_FQDN"         = var.sql_server_fqdn
      "AZURE_SQL_DATABASE_NAME"       = var.sql_database_name
      "AZURE_CLIENT_ID"               = var.identity_client_id
      "KEY_VAULT_URI"                 = var.key_vault_uri
      "APIM_GATEWAY_URL"              = var.apim_gateway_url
      "AWR_API_KEY"                   = var.use_key_vault_secret_refs ? "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/awr-api-key)" : var.awr_api_key
    },
    var.extra_app_settings
  )

  tags = var.tags
}
