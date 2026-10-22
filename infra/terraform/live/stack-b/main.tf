# =============================================================================
# Live Root: Stack B (.NET Blazor WASM)
# =============================================================================
# Deploys Stack B app host consuming shared outputs. Does not manage Stack A.

locals {
  tags = {
    environment = var.environment
    project     = var.project_name
    stack       = "stack-b"
    managedBy   = "terraform"
  }

  stack_b_runtime_app_settings = merge(
    local.bulk_scoring_app_settings,
    {
      APP_AUTH_MODE                                   = var.app_auth_mode
      AZURE_TENANT_ID                                 = var.tenant_id
      ENTRA_API_APP_CLIENT_ID                         = var.entra_api_app_client_id
      ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID           = var.entra_api_service_principal_object_id
      ENTRA_API_IDENTIFIER_URI                        = var.entra_api_identifier_uri
      ENTRA_API_SCOPE                                 = var.entra_api_scope
      ENTRA_STACK_A_CLIENT_ID                         = var.entra_stack_a_client_id
      ENTRA_STACK_B_CLIENT_ID                         = var.entra_spa_client_id
      ENTRA_ADMIN_APP_ROLE_ID                         = var.app_auth_mode == "entra" ? var.entra_app_role_ids["admin"] : ""
      ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID            = var.app_auth_mode == "entra" ? var.entra_app_role_ids["organization_admin"] : ""
      ENTRA_RECRUITER_APP_ROLE_ID                     = var.app_auth_mode == "entra" ? var.entra_app_role_ids["recruiter"] : ""
      ENTRA_BUSINESS_PANEL_APP_ROLE_ID                = var.app_auth_mode == "entra" ? var.entra_app_role_ids["business_panel"] : ""
      ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID                 = var.entra_bootstrap_admin_object_id
      AWR_SEQ_API_ENDPOINT                            = var.awr_seq_api_endpoint
      AWR_AUTH_MODE                                   = var.awr_auth_mode
      AWR_AAD_AUDIENCE                                = var.awr_aad_audience
      AWR_MAX_PARALLEL                                = tostring(var.awr_max_parallel)
      AWR_SCORING_LEASE_SECONDS                       = tostring(var.awr_scoring_lease_seconds)
      CORS_ALLOWED_ORIGINS                            = var.cors_allowed_origins
      API_MODE                                        = var.api_mode
      PRODUCT_ENVIRONMENT                             = var.product_environment
      MICROSOFT365_SOURCE_MODE                        = var.microsoft365_source_mode
      MICROSOFT365_SOURCE_PUBLISHER_VERIFIED          = var.microsoft365_source_publisher_verified
      MICROSOFT365_SOURCE_CALLBACK_PATH               = var.microsoft365_source_callback_path
      MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH = var.microsoft365_source_admin_consent_callback_path
      MICROSOFT365_SOURCE_AUTHORITY                   = var.microsoft365_source_authority
      MICROSOFT365_SOURCE_SCOPES                      = var.microsoft365_source_scopes
      MICROSOFT_GRAPH_BASE_URI                        = var.microsoft_graph_base_uri
      MICROSOFT365_SOURCE_MAX_CONCURRENCY             = var.microsoft365_source_max_concurrency
      BULK_SOURCE_IDLE_TTL_MINUTES                    = var.bulk_source_idle_ttl_minutes
      BULK_SOURCE_ABSOLUTE_TTL_MINUTES                = var.bulk_source_absolute_ttl_minutes
      BULK_DISCOVERY_STEP_FILES                       = var.bulk_discovery_step_files
      BULK_DISCOVERY_STEP_MAX_ENTRIES                 = var.bulk_discovery_step_max_entries
      BULK_DISCOVERY_STEP_MAX_SECONDS                 = var.bulk_discovery_step_max_seconds
      BULK_MAX_TOTAL_BYTES                            = var.bulk_max_total_bytes
      BULK_PENDING_CONNECTIONS_PER_ACTOR              = var.bulk_pending_connections_per_actor
      BULK_CONNECTION_STARTS_PER_ACTOR_PER_MINUTE     = var.bulk_connection_starts_per_actor_per_minute
      BULK_CONNECTION_STARTS_PER_IP_PER_MINUTE        = var.bulk_connection_starts_per_ip_per_minute
      BULK_CALLBACKS_PER_IP_PER_MINUTE                = var.bulk_callbacks_per_ip_per_minute
      BULK_ADMISSION_HMAC_KEY_URI                     = var.bulk_admission_hmac_key_uri
      BULK_IMPORT_CONCURRENCY                         = var.bulk_import_concurrency
      BULK_CONVERT_CONCURRENCY                        = var.bulk_convert_concurrency
      BULK_RASTER_DPI                                 = var.bulk_raster_dpi
    }
  )

  bulk_scoring_app_settings = var.bulk_auth_key_uri != "" ? {
    BULK_AUTH_KEY_URI = var.bulk_auth_key_uri
  } : {}
}

module "stack_b" {
  source = "../../modules/stack-b"

  environment                    = var.environment
  location                       = var.location
  resource_group_name            = var.resource_group_name
  project_name                   = var.project_name
  app_name_infix                 = var.stack_b_app_name_infix
  app_service_plan_id            = var.app_service_plan_id
  identity_id                    = var.identity_id
  identity_client_id             = var.identity_client_id
  key_vault_uri                  = var.key_vault_uri
  microsoft365_source_client_id  = var.microsoft365_source_client_id
  sql_server_fqdn                = var.sql_server_fqdn
  sql_database_name              = var.sql_database_name
  apim_gateway_url               = var.apim_gateway_url
  app_insights_connection_string = var.app_insights_connection_string
  use_key_vault_secret_refs      = var.use_key_vault_secret_refs
  awr_api_key                    = var.awr_api_key
  extra_app_settings             = local.stack_b_runtime_app_settings

  virtual_network_subnet_id = var.integration_subnet_id != "" ? var.integration_subnet_id : null
  allowed_ips               = var.allowed_ips

  tags = local.tags
}
