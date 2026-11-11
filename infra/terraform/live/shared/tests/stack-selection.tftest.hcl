mock_provider "azuread" {
  mock_data "azuread_client_config" {
    defaults = {
      object_id = "44444444-4444-4444-4444-444444444444"
      tenant_id = "55555555-5555-5555-5555-555555555555"
    }
  }
  mock_data "azuread_service_principal" {
    defaults = {
      object_id    = "66666666-6666-6666-6666-666666666666"
      app_role_ids = { "TalentMatch.Access" = "77777777-7777-7777-7777-777777777777" }
    }
  }
}
mock_provider "azurerm" {
  mock_data "azurerm_mssql_server" {
    defaults = {
      id = "/subscriptions/88888888-8888-8888-8888-888888888888/resourceGroups/rg-data-test/providers/Microsoft.Sql/servers/sql-talentmatch-test"
    }
  }
}
mock_provider "azapi" {}

variables {
  subscription_id                                = "88888888-8888-8888-8888-888888888888"
  tenant_id                                      = "55555555-5555-5555-5555-555555555555"
  environment                                    = "test"
  resource_group_name                            = "rg-talentmatch-test"
  location                                       = "australiaeast"
  reuse_sql                                      = true
  existing_sql_server_name                       = "sql-talentmatch-test"
  existing_sql_database_name                     = "sqldb-talentmatch-test"
  existing_sql_rg                                = "rg-data-test"
  reuse_entra                                    = true
  existing_entra_api_app_client_id               = "11111111-1111-1111-1111-111111111111"
  existing_entra_api_service_principal_object_id = "22222222-2222-2222-2222-222222222222"
  existing_entra_stack_a_client_id               = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
  existing_entra_stack_b_client_id               = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
  entra_api_identifier_uri                       = "api://talentmatch-test"
  entra_api_scope_id                             = "00000000-0000-0000-0000-000000000010"
  entra_app_role_ids = {
    admin              = "00000000-0000-0000-0000-000000000011"
    organization_admin = "00000000-0000-0000-0000-000000000012"
    recruiter          = "00000000-0000-0000-0000-000000000013"
    business_panel     = "00000000-0000-0000-0000-000000000014"
  }
  entra_stack_a_redirect_uris = ["https://stack-a.example.com/"]
  entra_stack_b_redirect_uris = ["https://stack-b.example.com/"]
  awr_api_client_id           = "99999999-9999-9999-9999-999999999999"
}

run "stack_a_reuses_only_selected_objects" {
  command = plan
  variables {
    stack_target                     = "stack-a"
    reuse_identities                 = true
    existing_identity_stack_a_name   = "id-talentmatch-node-test"
    existing_identities_rg           = "rg-identities-test"
    existing_entra_stack_b_client_id = ""
    entra_stack_b_redirect_uris      = []
  }
  assert {
    condition     = output.entra_stack_a_client_id == var.existing_entra_stack_a_client_id && output.entra_stack_b_client_id == "" && output.identity_stack_b_id == ""
    error_message = "The shared root must not require or expose the unselected stack's SPA or identity."
  }
  assert {
    condition     = toset(keys(local.runtime_identity_principal_ids)) == toset(["stack_a"])
    error_message = "Connector federation, wrapping-key access and metrics publishing must exclude Stack B."
  }
  assert {
    condition     = length(data.azuread_service_principal.awr_api) == 0 && length(azuread_app_role_assignment.awr_api_stack_a) == 0 && length(azuread_app_role_assignment.awr_api_stack_b) == 0
    error_message = "A stale AWReason client ID must not enable Entra role assignments."
  }
}

run "stack_b_reuses_only_selected_objects" {
  command = plan
  variables {
    stack_target                     = "stack-b"
    reuse_identities                 = true
    existing_identity_stack_b_name   = "id-talentmatch-blazor-test"
    existing_identities_rg           = "rg-identities-test"
    existing_entra_stack_a_client_id = ""
    entra_stack_a_redirect_uris      = []
  }
  assert {
    condition     = output.entra_stack_b_client_id == var.existing_entra_stack_b_client_id && output.entra_stack_a_client_id == "" && output.identity_stack_a_id == ""
    error_message = "Stack B must not depend on Stack A identity or SPA coordinates."
  }
  assert {
    condition     = toset(keys(local.runtime_identity_principal_ids)) == toset(["stack_b"])
    error_message = "Connector federation, wrapping-key access and metrics publishing must exclude Stack A."
  }
}

run "apikey_does_not_assign_awr_roles" {
  command = plan
  variables {
    awr_auth_mode = "apikey"
  }
  assert {
    condition     = length(data.azuread_service_principal.awr_api) == 0 && length(azuread_app_role_assignment.awr_api_stack_a) == 0 && length(azuread_app_role_assignment.awr_api_stack_b) == 0
    error_message = "API-key authentication must not create managed-identity AWReason assignments."
  }
}

run "awr_entra_stack_a_only" {
  command = plan
  variables {
    stack_target  = "stack-a"
    awr_auth_mode = "entra"
  }
  assert {
    condition     = length(data.azuread_service_principal.awr_api) == 1 && length(azuread_app_role_assignment.awr_api_stack_a) == 1 && length(azuread_app_role_assignment.awr_api_stack_b) == 0
    error_message = "AWReason Entra access must be assigned only to the selected Stack A identity."
  }
}

run "awr_entra_stack_b_only" {
  command = plan
  variables {
    stack_target  = "stack-b"
    awr_auth_mode = "entra"
  }
  assert {
    condition     = length(data.azuread_service_principal.awr_api) == 1 && length(azuread_app_role_assignment.awr_api_stack_a) == 0 && length(azuread_app_role_assignment.awr_api_stack_b) == 1
    error_message = "AWReason Entra access must be assigned only to the selected Stack B identity."
  }
}

run "awr_entra_both_stacks" {
  command = plan
  variables {
    awr_auth_mode = "entra"
  }
  assert {
    condition     = length(azuread_app_role_assignment.awr_api_stack_a) == 1 && length(azuread_app_role_assignment.awr_api_stack_b) == 1
    error_message = "Both-stack environments must retain both AWReason role assignments."
  }
}

run "simple_auth_has_no_product_applications" {
  command = plan
  variables {
    app_auth_mode      = "simple"
    reuse_entra        = false
    entra_api_scope_id = ""
    entra_app_role_ids = {}
  }
  assert {
    condition     = output.entra_api_app_client_id == "" && output.entra_api_service_principal_object_id == "" && output.entra_stack_a_client_id == "" && output.entra_stack_b_client_id == "" && output.entra_api_scope_id == "" && length(output.entra_app_role_ids) == 0
    error_message = "The shared root must disable product Entra provisioning in simple-auth mode."
  }
}

run "shared_only_has_no_runtime_identities_or_awr_roles" {
  command = plan
  variables {
    stack_target  = "shared-only"
    awr_auth_mode = "entra"
  }
  assert {
    condition     = output.identity_stack_a_id == "" && output.identity_stack_b_id == "" && output.entra_stack_a_client_id == "" && output.entra_stack_b_client_id == "" && length(data.azuread_service_principal.awr_api) == 0
    error_message = "A shared-only environment must not provision runtime identities, SPAs or AWReason assignments."
  }
}
