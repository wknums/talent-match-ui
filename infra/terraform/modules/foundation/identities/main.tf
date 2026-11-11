# =============================================================================
# Foundation Module: Managed Identities (reuse-aware)
# =============================================================================

resource "terraform_data" "contract" {
  input = {
    reuse          = var.reuse
    enable_stack_a = var.enable_stack_a
    enable_stack_b = var.enable_stack_b
  }

  lifecycle {
    precondition {
      condition     = !var.reuse || !var.enable_stack_a || trimspace(var.existing_stack_a_name) != ""
      error_message = "existing_stack_a_name is required when reuse and enable_stack_a are true."
    }
    precondition {
      condition     = !var.reuse || !var.enable_stack_b || trimspace(var.existing_stack_b_name) != ""
      error_message = "existing_stack_b_name is required when reuse and enable_stack_b are true."
    }
    precondition {
      condition     = !var.reuse || (!var.enable_stack_a && !var.enable_stack_b) || trimspace(var.existing_resource_group) != ""
      error_message = "existing_resource_group is required when reusing any enabled stack identity."
    }
  }
}

# --- Data sources for reused identities ---
data "azurerm_user_assigned_identity" "stack_a" {
  count               = var.reuse && var.enable_stack_a && trimspace(var.existing_stack_a_name) != "" ? 1 : 0
  name                = var.existing_stack_a_name
  resource_group_name = var.existing_resource_group
}

data "azurerm_user_assigned_identity" "stack_b" {
  count               = var.reuse && var.enable_stack_b && trimspace(var.existing_stack_b_name) != "" ? 1 : 0
  name                = var.existing_stack_b_name
  resource_group_name = var.existing_resource_group
}

# --- Managed identities ---
resource "azurerm_user_assigned_identity" "stack_a" {
  count               = !var.reuse && var.enable_stack_a ? 1 : 0
  name                = var.stack_a_name
  location            = var.location
  resource_group_name = var.resource_group_name

  tags = var.tags
}

resource "azurerm_user_assigned_identity" "stack_b" {
  count               = !var.reuse && var.enable_stack_b ? 1 : 0
  name                = var.stack_b_name
  location            = var.location
  resource_group_name = var.resource_group_name

  tags = var.tags
}

# These hosting identities receive no Microsoft Graph permission. They authenticate the separate
# confidential source connector through reviewed federation only; Graph access remains delegated
# to the connected work/school user.
# infra/terraform/tests/assert-no-graph-app-roles.mjs fails a plan that adds an application role.
