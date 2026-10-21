mock_provider "azurerm" {}

run "reuse_stack_a_only" {
  command = plan

  variables {
    reuse                   = true
    enable_stack_a          = true
    enable_stack_b          = false
    existing_stack_a_name   = "id-stack-a"
    existing_resource_group = "identities-rg"
    location                = "swedencentral"
    resource_group_name     = "app-rg"
  }

  assert {
    condition     = output.stack_b_id == ""
    error_message = "Stack B output must be empty when Stack B is disabled."
  }
}

run "reuse_stack_b_only" {
  command = plan

  variables {
    reuse                   = true
    enable_stack_a          = false
    enable_stack_b          = true
    existing_stack_b_name   = "id-stack-b"
    existing_resource_group = "identities-rg"
    location                = "swedencentral"
    resource_group_name     = "app-rg"
  }

  assert {
    condition     = output.stack_a_id == ""
    error_message = "Stack A output must be empty when Stack A is disabled."
  }
}

run "create_stack_b_only" {
  command = plan

  variables {
    reuse               = false
    enable_stack_a      = false
    enable_stack_b      = true
    stack_b_name        = "id-stack-b"
    location            = "swedencentral"
    resource_group_name = "app-rg"
  }

  assert {
    condition     = output.stack_a_id == ""
    error_message = "Stack A must not be created when Stack A is disabled."
  }
}

run "shared_only" {
  command = plan

  variables {
    reuse               = true
    enable_stack_a      = false
    enable_stack_b      = false
    location            = "swedencentral"
    resource_group_name = "app-rg"
  }

  assert {
    condition     = output.stack_a_id == "" && output.stack_b_id == ""
    error_message = "Shared-only deployments must not require stack identities."
  }
}

run "enabled_stack_requires_name" {
  command = plan

  variables {
    reuse                   = true
    enable_stack_a          = true
    enable_stack_b          = false
    existing_resource_group = "identities-rg"
    location                = "swedencentral"
    resource_group_name     = "app-rg"
  }

  expect_failures = [
    terraform_data.contract,
  ]
}
