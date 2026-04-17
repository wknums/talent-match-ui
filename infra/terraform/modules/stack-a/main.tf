# =============================================================================
# Stack A Composition Module
# =============================================================================
# Composes a Node.js/Express App Service host for Stack A using the shared
# app-service building block and stable shared-root inputs.

module "app_service" {
  source = "../foundation/app-service"

  name                = "app-${var.project_name}-node-${var.environment}"
  location            = var.location
  resource_group_name = var.resource_group_name
  service_plan_id     = var.app_service_plan_id
  identity_id         = var.identity_id

  node_version = "20-lts"
  always_on    = var.environment != "dev"

  app_settings = merge(
    {
      "WEBSITE_NODE_DEFAULT_VERSION"   = "~20"
      "SCM_DO_BUILD_DURING_DEPLOYMENT" = "false"
      "STORAGE_PROVIDER"               = "azuresql"
      "KEY_VAULT_URI"                  = var.key_vault_uri
      "APIM_GATEWAY_URL"              = var.apim_gateway_url
      "AZURE_SQL_CONNECTION_STRING"    = "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/sql-connection-string)"
      "AWR_API_KEY"                    = "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/awr-api-key)"
      "OPENAI_API_KEY"                 = "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/openai-api-key)"
    },
    var.extra_app_settings
  )

  tags = var.tags
}
