data "azuread_client_config" "current" {}

locals {
  api_app_client_id               = var.reuse ? var.existing_api_app_client_id : azuread_application.api[0].client_id
  api_service_principal_object_id = var.reuse ? var.existing_api_service_principal_object_id : azuread_service_principal.api[0].object_id
  stack_a_client_id               = var.reuse ? var.existing_stack_a_client_id : azuread_application.stack_a[0].client_id
  stack_b_client_id               = var.reuse ? var.existing_stack_b_client_id : azuread_application.stack_b[0].client_id
  role_groups                     = var.create_role_groups ? var.role_group_names : {}
}

resource "terraform_data" "reuse_contract" {
  input = var.reuse

  lifecycle {
    precondition {
      condition = !var.reuse || alltrue([
        var.existing_api_app_client_id != "",
        var.existing_api_service_principal_object_id != "",
        var.existing_stack_a_client_id != "",
        var.existing_stack_b_client_id != ""
      ])
      error_message = "All existing Entra application and API service principal IDs are required when reuse is true."
    }
  }
}

resource "azuread_application" "api" {
  count = var.reuse ? 0 : 1

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
  count = var.reuse ? 0 : 1

  client_id                    = azuread_application.api[0].client_id
  app_role_assignment_required = false
}

resource "azuread_application" "stack_a" {
  count = var.reuse ? 0 : 1

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
  count = var.reuse ? 0 : 1

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
  count = var.reuse ? 0 : 1

  client_id = azuread_application.stack_a[0].client_id
}

resource "azuread_service_principal" "stack_b" {
  count = var.reuse ? 0 : 1

  client_id = azuread_application.stack_b[0].client_id
}

resource "azuread_group" "role" {
  for_each = local.role_groups

  display_name     = each.value
  security_enabled = true
}

resource "azuread_app_role_assignment" "role_group" {
  for_each = azuread_group.role

  app_role_id         = var.app_role_ids[each.key]
  principal_object_id = each.value.object_id
  resource_object_id  = local.api_service_principal_object_id
}

resource "azuread_app_role_assignment" "bootstrap_admin" {
  count = var.bootstrap_admin_object_id != "" ? 1 : 0

  app_role_id         = var.app_role_ids["admin"]
  principal_object_id = var.bootstrap_admin_object_id
  resource_object_id  = local.api_service_principal_object_id
}