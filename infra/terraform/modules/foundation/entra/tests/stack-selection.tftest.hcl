mock_provider "azuread" {
  mock_data "azuread_client_config" {
    defaults = {
      object_id = "44444444-4444-4444-4444-444444444444"
      tenant_id = "55555555-5555-5555-5555-555555555555"
    }
  }
}

variables {
  api_display_name   = "talentmatch-test-api"
  api_identifier_uri = "api://talentmatch-test"
  api_scope_id       = "00000000-0000-0000-0000-000000000010"
  app_role_ids = {
    admin              = "00000000-0000-0000-0000-000000000011"
    organization_admin = "00000000-0000-0000-0000-000000000012"
    recruiter          = "00000000-0000-0000-0000-000000000013"
    business_panel     = "00000000-0000-0000-0000-000000000014"
  }
  stack_a_display_name                     = "talentmatch-test-stack-a"
  stack_b_display_name                     = "talentmatch-test-stack-b"
  stack_a_redirect_uris                    = ["https://stack-a.example.com/"]
  stack_b_redirect_uris                    = ["https://stack-b.example.com/"]
  existing_api_app_client_id               = "11111111-1111-1111-1111-111111111111"
  existing_api_service_principal_object_id = "22222222-2222-2222-2222-222222222222"
  existing_stack_a_client_id               = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
  existing_stack_b_client_id               = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
}

run "create_stack_a_only" {
  command = plan
  variables {
    enable_stack_b        = false
    stack_b_redirect_uris = []
  }
  assert {
    condition     = length(azuread_application.api) == 1 && length(azuread_service_principal.api) == 1
    error_message = "A single-stack deployment still needs one shared protected API."
  }
  assert {
    condition     = length(azuread_application.stack_a) == 1 && length(azuread_service_principal.stack_a) == 1 && length(azuread_application.stack_b) == 0 && length(azuread_service_principal.stack_b) == 0 && output.stack_b_client_id == ""
    error_message = "Stack A must not create or expose a Stack B SPA."
  }
  assert {
    condition     = length(azuread_application.source_connector) == 0 && length(azuread_application_federated_identity_credential.source_connector) == 0
    error_message = "The optional connector must remain absent when disabled."
  }
}

run "create_stack_b_only" {
  command = plan
  variables {
    enable_stack_a        = false
    stack_a_redirect_uris = []
  }
  assert {
    condition     = length(azuread_application.api) == 1 && length(azuread_application.stack_b) == 1 && length(azuread_service_principal.stack_b) == 1 && length(azuread_application.stack_a) == 0 && length(azuread_service_principal.stack_a) == 0 && output.stack_a_client_id == ""
    error_message = "Stack B needs the shared API and its own SPA, not a Stack A SPA."
  }
}

run "create_both_stacks" {
  command = plan
  assert {
    condition     = length(azuread_application.api) == 1 && length(azuread_application.stack_a) == 1 && length(azuread_application.stack_b) == 1 && length(azuread_service_principal.stack_a) == 1 && length(azuread_service_principal.stack_b) == 1
    error_message = "Both-stack deployments must retain all three product registrations."
  }
}

run "reuse_stack_a_only" {
  command = plan
  variables {
    reuse                      = true
    enable_stack_b             = false
    existing_stack_b_client_id = ""
  }
  assert {
    condition     = length(azuread_application.api) == 0 && length(azuread_application.stack_a) == 0 && length(azuread_application.stack_b) == 0 && output.stack_a_client_id == var.existing_stack_a_client_id && output.stack_b_client_id == ""
    error_message = "Reusing Stack A must require and expose only its own SPA."
  }
}

run "reuse_stack_b_only" {
  command = plan
  variables {
    reuse                      = true
    enable_stack_a             = false
    existing_stack_a_client_id = ""
  }
  assert {
    condition     = length(azuread_application.api) == 0 && length(azuread_application.stack_a) == 0 && length(azuread_application.stack_b) == 0 && output.stack_b_client_id == var.existing_stack_b_client_id && output.stack_a_client_id == ""
    error_message = "Reusing Stack B must require and expose only its own SPA."
  }
}

run "reuse_both_stacks" {
  command = plan
  variables {
    reuse = true
  }
  assert {
    condition     = length(azuread_application.api) == 0 && length(azuread_application.stack_a) == 0 && length(azuread_application.stack_b) == 0 && output.stack_a_client_id == var.existing_stack_a_client_id && output.stack_b_client_id == var.existing_stack_b_client_id
    error_message = "Reuse must not create product applications."
  }
}

run "missing_selected_spa_is_rejected" {
  command = plan
  variables {
    reuse                      = true
    enable_stack_a             = false
    existing_stack_b_client_id = ""
  }
  expect_failures = [terraform_data.reuse_contract]
}

run "both_stacks_require_both_reused_spas" {
  command = plan
  variables {
    reuse                      = true
    existing_stack_a_client_id = ""
  }
  expect_failures = [terraform_data.reuse_contract]
}

run "simple_auth_creates_no_product_objects" {
  command = plan
  variables {
    enabled                   = false
    api_scope_id              = ""
    app_role_ids              = {}
    create_role_groups        = true
    role_group_names          = { admin = "talentmatch-test-admin" }
    bootstrap_admin_object_id = "33333333-3333-3333-3333-333333333333"
  }
  assert {
    condition     = length(azuread_application.api) == 0 && length(azuread_service_principal.api) == 0 && length(azuread_application.stack_a) == 0 && length(azuread_application.stack_b) == 0 && length(azuread_group.role) == 0 && length(azuread_app_role_assignment.bootstrap_admin) == 0 && output.api_app_client_id == "" && output.stack_a_client_id == "" && output.stack_b_client_id == ""
    error_message = "Disabled product Entra authentication must not provision product objects, even with stale inputs."
  }
}

run "missing_scope_id_is_rejected" {
  command = plan
  variables {
    api_scope_id = ""
  }
  expect_failures = [var.api_scope_id]
}

run "malformed_scope_id_is_rejected" {
  command = plan
  variables {
    api_scope_id = "not-a-uuid"
  }
  expect_failures = [var.api_scope_id]
}

run "missing_role_ids_are_rejected" {
  command = plan
  variables {
    app_role_ids = {}
  }
  expect_failures = [var.app_role_ids]
}

run "malformed_role_id_is_rejected" {
  command = plan
  variables {
    app_role_ids = {
      admin              = "not-a-uuid"
      organization_admin = "00000000-0000-0000-0000-000000000012"
      recruiter          = "00000000-0000-0000-0000-000000000013"
      business_panel     = "00000000-0000-0000-0000-000000000014"
    }
  }
  expect_failures = [var.app_role_ids]
}

run "duplicate_role_ids_are_rejected" {
  command = plan
  variables {
    app_role_ids = {
      admin              = "00000000-0000-0000-0000-000000000011"
      organization_admin = "00000000-0000-0000-0000-000000000011"
      recruiter          = "00000000-0000-0000-0000-000000000013"
      business_panel     = "00000000-0000-0000-0000-000000000014"
    }
  }
  expect_failures = [var.app_role_ids]
}

run "shared_only_has_no_spas" {
  command = plan
  variables {
    enable_stack_a = false
    enable_stack_b = false
  }
  assert {
    condition     = length(azuread_application.api) == 1 && length(azuread_application.stack_a) == 0 && length(azuread_application.stack_b) == 0 && output.stack_a_client_id == "" && output.stack_b_client_id == ""
    error_message = "A shared-only profile must not provision either frontend."
  }
}

run "connector_federates_only_selected_identity" {
  command = plan
  variables {
    enable_stack_a                 = false
    source_connector_enabled       = true
    source_connector_redirect_uris = ["https://stack-b.example.com/api/bulk-source/callback"]
    source_connector_federated_identity_principal_ids = {
      stack_b = "66666666-6666-6666-6666-666666666666"
    }
  }
  assert {
    condition     = length(azuread_application.source_connector) == 1 && length(azuread_service_principal.source_connector) == 1 && toset(keys(azuread_application_federated_identity_credential.source_connector)) == toset(["stack_b"])
    error_message = "An enabled connector needs only the selected stack's federated credential."
  }
}

run "disabled_connector_ignores_stale_coordinates" {
  command = plan
  variables {
    reuse_source_connector              = true
    existing_source_connector_client_id = "77777777-7777-7777-7777-777777777777"
    source_connector_federated_identity_principal_ids = {
      stack_a = "66666666-6666-6666-6666-666666666666"
    }
  }

  assert {
    condition     = length(azuread_application.source_connector) == 0 && length(data.azuread_application.existing_source_connector) == 0 && length(azuread_application_federated_identity_credential.source_connector) == 0 && output.source_connector_client_id == ""
    error_message = "Stale connector inputs must not enable connector provisioning or lookup."
  }
}

run "plan_new_role_groups" {
  command = plan
  variables {
    create_role_groups = true
    role_group_names = {
      admin     = "talentmatch-test-admin"
      recruiter = "talentmatch-test-recruiter"
    }
  }
  assert {
    condition     = toset(keys(azuread_group.role)) == toset(["admin", "recruiter"]) && toset(keys(azuread_app_role_assignment.role_group)) == toset(keys(azuread_group.role))
    error_message = "New groups and their assignments must have the configured role keys before object IDs are known."
  }
}

run "plan_role_groups_with_reused_api" {
  command = plan
  variables {
    reuse              = true
    create_role_groups = true
    role_group_names = {
      business_panel = "talentmatch-test-business-panel"
    }
  }
  assert {
    condition     = toset(keys(azuread_app_role_assignment.role_group)) == toset(["business_panel"]) && azuread_app_role_assignment.role_group["business_panel"].resource_object_id == var.existing_api_service_principal_object_id
    error_message = "Role groups must also be assigned when the protected API is reused."
  }
}

run "disabled_role_groups_ignore_stale_names" {
  command = plan
  variables {
    create_role_groups = false
    role_group_names = {
      admin = "talentmatch-test-admin"
    }
  }
  assert {
    condition     = length(azuread_group.role) == 0 && length(azuread_app_role_assignment.role_group) == 0
    error_message = "Disabled group creation must not create groups or assignments from stale names."
  }
}

run "disabled_auth_has_no_group_assignments" {
  command = plan
  variables {
    enabled            = false
    create_role_groups = true
    role_group_names = {
      admin = "talentmatch-test-admin"
    }
  }
  assert {
    condition     = length(azuread_group.role) == 0 && length(azuread_app_role_assignment.role_group) == 0
    error_message = "Disabled product Entra authentication must not create role group assignments."
  }
}

run "apply_role_group_bindings" {
  command = apply
  variables {
    reuse              = true
    create_role_groups = true
    role_group_names = {
      admin     = "talentmatch-test-admin"
      recruiter = "talentmatch-test-recruiter"
    }
  }
  override_resource {
    target = azuread_group.role["admin"]
    values = {
      object_id = "66666666-6666-6666-6666-666666666666"
    }
  }
  override_resource {
    target = azuread_group.role["recruiter"]
    values = {
      object_id = "77777777-7777-7777-7777-777777777777"
    }
  }
  assert {
    condition = alltrue([
      for role, assignment in azuread_app_role_assignment.role_group :
      assignment.principal_object_id == azuread_group.role[role].object_id &&
      assignment.app_role_id == var.app_role_ids[role] &&
      assignment.resource_object_id == var.existing_api_service_principal_object_id
    ])
    error_message = "Each assignment must bind its own group object ID and role ID to the selected API service principal."
  }
}
