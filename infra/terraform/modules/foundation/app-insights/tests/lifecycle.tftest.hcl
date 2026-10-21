mock_provider "azurerm" {
  mock_data "azurerm_application_insights" {
    defaults = {
      id                = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/shared-monitoring-rg/providers/Microsoft.Insights/components/shared-insights"
      connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000002"
    }
  }

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/app-rg/providers/Microsoft.OperationalInsights/workspaces/law-test"
    }
  }

  mock_resource "azurerm_application_insights" {
    defaults = {
      id                = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/app-rg/providers/Microsoft.Insights/components/appi-test"
      connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000003"
    }
  }
}

variables {
  name                = "appi-test"
  workspace_name      = "law-test"
  location            = "australiaeast"
  resource_group_name = "app-rg"
  publisher_principal_ids = {
    stack_a = "00000000-0000-0000-0000-000000000004"
    stack_b = "00000000-0000-0000-0000-000000000005"
  }
}

run "disabled" {
  command = plan

  assert {
    condition = (
      length(data.azurerm_application_insights.existing) == 0 &&
      length(azurerm_application_insights.main) == 0 &&
      length(azurerm_log_analytics_workspace.main) == 0 &&
      length(azurerm_role_assignment.publisher) == 0 &&
      output.id == "" && output.connection_string == ""
    )
    error_message = "Disabled monitoring must perform no lookup, create no resources and expose empty outputs."
  }
}

run "disabled_ignores_reuse" {
  command = plan

  variables {
    reuse = true
  }

  assert {
    condition     = length(data.azurerm_application_insights.existing) == 0 && output.connection_string == ""
    error_message = "A disabled integration must not require reuse coordinates."
  }
}

run "create_for_both_stacks" {
  command = apply

  variables {
    enabled = true
  }

  assert {
    condition = (
      length(azurerm_application_insights.main) == 1 &&
      length(azurerm_log_analytics_workspace.main) == 1 &&
      length(data.azurerm_application_insights.existing) == 0 &&
      length(azurerm_role_assignment.publisher) == 2
    )
    error_message = "Both stacks must share exactly one new component and workspace."
  }

  assert {
    condition = (
      azurerm_application_insights.main[0].workspace_id == azurerm_log_analytics_workspace.main[0].id &&
      !azurerm_application_insights.main[0].local_authentication_enabled &&
      azurerm_log_analytics_workspace.main[0].retention_in_days == 30 &&
      output.connection_string == azurerm_application_insights.main[0].connection_string
    )
    error_message = "New monitoring must use its managed workspace, Entra-only ingestion and a stable connection-string output."
  }

  assert {
    condition = alltrue([
      for role in azurerm_role_assignment.publisher :
      role.scope == output.id && role.role_definition_name == "Monitoring Metrics Publisher"
    ])
    error_message = "Both identities must publish with least-privilege component-scoped RBAC."
  }
}

run "disable_managed_monitoring" {
  command = apply

  assert {
    condition = (
      length(azurerm_application_insights.main) == 0 &&
      length(azurerm_log_analytics_workspace.main) == 0 &&
      length(azurerm_role_assignment.publisher) == 0 &&
      output.id == "" && output.connection_string == ""
    )
    error_message = "Disabling must remove managed monitoring resources and assignments from the preceding create run."
  }
}

run "reuse_external_resource_group" {
  command = plan

  variables {
    enabled                 = true
    reuse                   = true
    existing_name           = "shared-insights"
    existing_resource_group = "shared-monitoring-rg"
  }

  assert {
    condition = (
      length(azurerm_application_insights.main) == 0 &&
      length(azurerm_log_analytics_workspace.main) == 0 &&
      length(data.azurerm_application_insights.existing) == 1 &&
      data.azurerm_application_insights.existing[0].resource_group_name == "shared-monitoring-rg" &&
      output.id == data.azurerm_application_insights.existing[0].id &&
      output.connection_string == data.azurerm_application_insights.existing[0].connection_string &&
      length(azurerm_role_assignment.publisher) == 2
    )
    error_message = "Reuse must resolve the external component without managing it or its workspace."
  }
}

run "single_stack" {
  command = plan

  variables {
    enabled                 = true
    reuse                   = true
    existing_name           = "shared-insights"
    existing_resource_group = "shared-monitoring-rg"
    publisher_principal_ids = {
      stack_b = "00000000-0000-0000-0000-000000000005"
    }
  }

  assert {
    condition     = keys(azurerm_role_assignment.publisher) == ["stack_b"]
    error_message = "A single-stack deployment must not grant the other identity access."
  }
}

run "shared_only" {
  command = plan

  variables {
    enabled                 = true
    publisher_principal_ids = {}
  }

  assert {
    condition     = length(azurerm_role_assignment.publisher) == 0
    error_message = "Shared-only monitoring must not require stack identities."
  }
}

run "missing_reuse_name" {
  command = plan

  variables {
    enabled                 = true
    reuse                   = true
    existing_resource_group = "shared-monitoring-rg"
  }

  expect_failures = [var.existing_name]
}

run "missing_reuse_resource_group" {
  command = plan

  variables {
    enabled       = true
    reuse         = true
    existing_name = "shared-insights"
  }

  expect_failures = [var.existing_resource_group]
}
