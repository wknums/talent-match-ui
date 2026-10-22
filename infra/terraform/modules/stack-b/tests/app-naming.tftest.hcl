mock_provider "azurerm" {}

variables {
  environment         = "test"
  location            = "australiaeast"
  resource_group_name = "app-rg"
  app_service_plan_id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/app-rg/providers/Microsoft.Web/serverFarms/plan-test"
  identity_id         = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/app-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-test"
  identity_client_id  = "00000000-0000-0000-0000-000000000004"
  key_vault_uri       = ""
  sql_server_fqdn     = "test.database.windows.net"
  sql_database_name   = "test-db"
  apim_gateway_url    = ""
}

run "default_name_is_unchanged" {
  command = plan
  assert {
    condition     = output.app_name == "app-talentmatch-blazor-test"
    error_message = "Stack B must retain its existing App Service name by default."
  }
}

run "custom_infix_replaces_blazor" {
  command = plan
  variables {
    project_name   = "example"
    app_name_infix = "customer-ui"
  }
  assert {
    condition     = output.app_name == "app-example-customer-ui-test"
    error_message = "Stack B must use the configured infix without changing the project or environment."
  }
}

run "sixty_character_name_is_allowed" {
  command = plan
  variables {
    app_name_infix = join("", [for i in range(39) : "a"])
  }
  assert {
    condition     = length(output.app_name) == 60
    error_message = "The generated App Service name may contain exactly 60 characters."
  }
}

run "long_name_is_rejected" {
  command = plan
  variables {
    app_name_infix = join("", [for i in range(40) : "a"])
  }
  expect_failures = [var.app_name_infix]
}

run "empty_infix_is_rejected" {
  command = plan
  variables {
    app_name_infix = ""
  }
  expect_failures = [var.app_name_infix]
}

run "invalid_characters_are_rejected" {
  command = plan
  variables {
    app_name_infix = "customer_ui"
  }
  expect_failures = [var.app_name_infix]
}

run "uppercase_is_rejected" {
  command = plan
  variables {
    app_name_infix = "Customer"
  }
  expect_failures = [var.app_name_infix]
}

run "leading_hyphen_is_rejected" {
  command = plan
  variables {
    app_name_infix = "-customer"
  }
  expect_failures = [var.app_name_infix]
}

run "trailing_hyphen_is_rejected" {
  command = plan
  variables {
    app_name_infix = "customer-"
  }
  expect_failures = [var.app_name_infix]
}
