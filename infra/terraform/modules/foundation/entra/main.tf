data "azuread_client_config" "current" {}

locals {
  api_app_client_id               = var.enabled ? (var.reuse ? var.existing_api_app_client_id : azuread_application.api[0].client_id) : ""
  api_service_principal_object_id = var.enabled ? (var.reuse ? var.existing_api_service_principal_object_id : azuread_service_principal.api[0].object_id) : ""
  stack_a_client_id               = var.enabled && var.enable_stack_a ? (var.reuse ? var.existing_stack_a_client_id : azuread_application.stack_a[0].client_id) : ""
  stack_b_client_id               = var.enabled && var.enable_stack_b ? (var.reuse ? var.existing_stack_b_client_id : azuread_application.stack_b[0].client_id) : ""
  source_connector_client_id      = var.source_connector_enabled ? (var.reuse_source_connector ? var.existing_source_connector_client_id : azuread_application.source_connector[0].client_id) : ""
  role_groups                     = var.enabled && var.create_role_groups ? var.role_group_names : {}

}

data "azuread_application" "existing_source_connector" {
  count = var.source_connector_enabled && var.reuse_source_connector ? 1 : 0

  client_id = var.existing_source_connector_client_id
}

# One confidential multi-tenant authorization-code connector for SharePoint and work/school
# OneDrive. It has Web callbacks and delegated Graph scopes only. Managed identities authenticate
# this confidential client through federation; no password or certificate is created.
resource "azuread_application" "source_connector" {
  count = var.source_connector_enabled && !var.reuse_source_connector ? 1 : 0

  display_name     = var.source_connector_display_name
  owners           = [data.azuread_client_config.current.object_id]
  sign_in_audience = "AzureADMultipleOrgs"

  web {
    redirect_uris = var.source_connector_redirect_uris
  }

  required_resource_access {
    resource_app_id = "00000003-0000-0000-c000-000000000000"

    resource_access {
      id   = "df85f4d6-205c-4ac5-a5ea-6bf408dba283"
      type = "Scope"
    }

    resource_access {
      id   = "205e70e5-aba6-4c52-a976-6d2d46c48043"
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "source_connector" {
  count = var.source_connector_enabled && !var.reuse_source_connector ? 1 : 0

  client_id = azuread_application.source_connector[0].client_id
}

resource "azuread_application_federated_identity_credential" "source_connector" {
  for_each = var.source_connector_enabled ? var.source_connector_federated_identity_principal_ids : {}

  application_id = var.reuse_source_connector ? data.azuread_application.existing_source_connector[0].id : azuread_application.source_connector[0].id
  display_name   = "source-connector-${each.key}"
  description    = "Client assertion for the delegated Microsoft 365 source connector on ${each.key}"
  audiences      = ["api://AzureADTokenExchange"]
  issuer         = "https://login.microsoftonline.com/${data.azuread_client_config.current.tenant_id}/v2.0"
  subject        = each.value
}

resource "terraform_data" "source_connector_contract" {
  input = var.source_connector_enabled

  lifecycle {
    precondition {
      condition     = !var.source_connector_enabled || length(var.source_connector_redirect_uris) > 0
      error_message = "At least one exact Web callback is required when the source connector is enabled."
    }
    precondition {
      condition     = !var.source_connector_enabled || !var.reuse_source_connector || var.existing_source_connector_client_id != ""
      error_message = "existing_source_connector_client_id is required when the source connector is reused."
    }
  }
}

resource "terraform_data" "reuse_contract" {
  input = var.reuse

  lifecycle {
    precondition {
      condition     = !var.enabled || trimspace(var.api_identifier_uri) != ""
      error_message = "api_identifier_uri is required when product Entra authentication is enabled."
    }
    precondition {
      condition     = !var.enabled || !var.enable_stack_a || length(var.stack_a_redirect_uris) > 0
      error_message = "Stack A redirect URIs are required when product Entra authentication and Stack A are enabled."
    }
    precondition {
      condition     = !var.enabled || !var.enable_stack_b || length(var.stack_b_redirect_uris) > 0
      error_message = "Stack B redirect URIs are required when product Entra authentication and Stack B are enabled."
    }
    precondition {
      condition = !var.enabled || !var.reuse || alltrue([
        var.existing_api_app_client_id != "",
        var.existing_api_service_principal_object_id != "",
        !var.enable_stack_a || var.existing_stack_a_client_id != "",
        !var.enable_stack_b || var.existing_stack_b_client_id != ""
      ])
      error_message = "The protected API client/service principal IDs and each enabled stack's SPA client ID are required when Entra authentication and reuse are enabled."
    }
  }
}

resource "azuread_application" "api" {
  count = var.enabled && !var.reuse ? 1 : 0

  display_name     = var.api_display_name
  identifier_uris  = [var.api_identifier_uri]
  owners           = [data.azuread_client_config.current.object_id]
  sign_in_audience = "AzureADMyOrg"

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      admin_consent_description  = "Allow the application to access TalentMatch as the signed-in user."
      admin_consent_display_name = "Access TalentMatch as a user"
      enabled                    = true
      id                         = var.api_scope_id
      type                       = "Admin"
      user_consent_description   = "Allow this application to access TalentMatch on your behalf."
      user_consent_display_name  = "Access TalentMatch"
      value                      = var.api_scope_value
    }
  }

  app_role {
    allowed_member_types = ["User"]
    description          = "Full platform administration across all organizations."
    display_name         = "Admin"
    enabled              = true
    id                   = var.app_role_ids["admin"]
    value                = "admin"
  }

  app_role {
    allowed_member_types = ["User"]
    description          = "Delegated administration within assigned organizations."
    display_name         = "Organization Admin"
    enabled              = true
    id                   = var.app_role_ids["organization_admin"]
    value                = "organization_admin"
  }

  app_role {
    allowed_member_types = ["User"]
    description          = "Recruiting operations within assigned organization and department scopes."
    display_name         = "Recruiter"
    enabled              = true
    id                   = var.app_role_ids["recruiter"]
    value                = "recruiter"
  }

  app_role {
    allowed_member_types = ["User"]
    description          = "Business panel review within assigned organization and department scopes."
    display_name         = "Business Panel"
    enabled              = true
    id                   = var.app_role_ids["business_panel"]
    value                = "business_panel"
  }
}

resource "azuread_service_principal" "api" {
  count = var.enabled && !var.reuse ? 1 : 0

  client_id                    = azuread_application.api[0].client_id
  app_role_assignment_required = false
}

resource "azuread_application" "stack_a" {
  count = var.enabled && var.enable_stack_a && !var.reuse ? 1 : 0

  display_name     = var.stack_a_display_name
  owners           = [data.azuread_client_config.current.object_id]
  sign_in_audience = "AzureADMyOrg"

  single_page_application {
    redirect_uris = var.stack_a_redirect_uris
  }

  required_resource_access {
    resource_app_id = local.api_app_client_id

    resource_access {
      id   = var.api_scope_id
      type = "Scope"
    }
  }
}

resource "azuread_application" "stack_b" {
  count = var.enabled && var.enable_stack_b && !var.reuse ? 1 : 0

  display_name     = var.stack_b_display_name
  owners           = [data.azuread_client_config.current.object_id]
  sign_in_audience = "AzureADMyOrg"

  single_page_application {
    redirect_uris = var.stack_b_redirect_uris
  }

  required_resource_access {
    resource_app_id = local.api_app_client_id

    resource_access {
      id   = var.api_scope_id
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "stack_a" {
  count = var.enabled && var.enable_stack_a && !var.reuse ? 1 : 0

  client_id = azuread_application.stack_a[0].client_id
}

resource "azuread_service_principal" "stack_b" {
  count = var.enabled && var.enable_stack_b && !var.reuse ? 1 : 0

  client_id = azuread_application.stack_b[0].client_id
}

resource "azuread_group" "role" {
  for_each = local.role_groups

  display_name     = each.value
  security_enabled = true
}

resource "azuread_app_role_assignment" "role_group" {
  for_each = local.role_groups

  app_role_id         = var.app_role_ids[each.key]
  principal_object_id = azuread_group.role[each.key].object_id
  resource_object_id  = local.api_service_principal_object_id
}

resource "azuread_app_role_assignment" "bootstrap_admin" {
  count = var.enabled && var.bootstrap_admin_object_id != "" ? 1 : 0

  app_role_id         = var.app_role_ids["admin"]
  principal_object_id = var.bootstrap_admin_object_id
  resource_object_id  = local.api_service_principal_object_id
}