mock_provider "azurerm" {}

variables {
  name                = "app-test-node-test"
  location            = "australiaeast"
  resource_group_name = "app-rg"
  service_plan_id     = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/app-rg/providers/Microsoft.Web/serverFarms/plan-test"
  identity_id         = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/app-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-test"
  identity_client_id  = "00000000-0000-0000-0000-000000000004"
  node_version        = "22-lts"
  app_settings = {
    EXISTING_SETTING = "preserved"
  }
}

run "disabled_preserves_runtime_settings" {
  command = plan

  assert {
    condition = (
      length(azurerm_linux_web_app.main.app_settings) == 1 &&
      azurerm_linux_web_app.main.app_settings["EXISTING_SETTING"] == "preserved" &&
      !contains(keys(azurerm_linux_web_app.main.app_settings), "APPLICATIONINSIGHTS_CONNECTION_STRING") &&
      !contains(keys(azurerm_linux_web_app.main.app_settings), "ApplicationInsightsAgent_EXTENSION_VERSION")
    )
    error_message = "Disabled monitoring must leave existing runtime configuration unchanged."
  }
}

run "node_monitoring" {
  command = plan

  variables {
    app_insights_connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000002"
  }

  assert {
    condition = (
      azurerm_linux_web_app.main.app_settings["APPLICATIONINSIGHTS_CONNECTION_STRING"] == var.app_insights_connection_string &&
      azurerm_linux_web_app.main.app_settings["ApplicationInsightsAgent_EXTENSION_VERSION"] == "~3" &&
      azurerm_linux_web_app.main.app_settings["APPLICATIONINSIGHTS_AUTHENTICATION_STRING"] == "Authorization=AAD;ClientId=${var.identity_client_id}" &&
      azurerm_linux_web_app.main.app_settings["OTEL_SERVICE_NAME"] == var.name &&
      azurerm_linux_web_app.main.app_settings["APPINSIGHTS_JAVASCRIPT_ENABLED"] == "false" &&
      azurerm_linux_web_app.main.app_settings["EXISTING_SETTING"] == "preserved"
    )
    error_message = "Node monitoring requires the Linux agent, shared destination, stack identity and distinct service name."
  }
}

run "dotnet_monitoring" {
  command = plan

  variables {
    name                           = "app-test-blazor-test"
    node_version                   = ""
    dotnet_version                 = "10.0"
    app_insights_connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000002"
  }

  assert {
    condition = (
      azurerm_linux_web_app.main.app_settings["APPLICATIONINSIGHTS_CONNECTION_STRING"] == var.app_insights_connection_string &&
      azurerm_linux_web_app.main.app_settings["OTEL_SERVICE_NAME"] == "app-test-blazor-test" &&
      azurerm_linux_web_app.main.app_settings["XDT_MicrosoftApplicationInsights_Mode"] == "recommended" &&
      azurerm_linux_web_app.main.app_settings["XDT_MicrosoftApplicationInsights_PreemptSdk"] == "1" &&
      azurerm_linux_web_app.main.app_settings["ApplicationInsightsAgent_EXTENSION_VERSION"] == "~3"
    )
    error_message = "Stack B must enable ASP.NET Core autoinstrumentation against the same shared destination."
  }
}

run "monitoring_requires_managed_identity_client" {
  command = plan

  variables {
    identity_client_id             = ""
    app_insights_connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000002"
  }

  expect_failures = [var.identity_client_id]
}
