import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

const MODULE_DIR = 'infra/terraform/modules/foundation/entra';
const LIVE_DIR = 'infra/terraform/live/shared';

const readTf = (dir: string, file: string): string =>
  readFileSync(resolve(dir, file), 'utf8');

const moduleMain = readTf(MODULE_DIR, 'main.tf');
const moduleVariables = readTf(MODULE_DIR, 'variables.tf');
const moduleOutputs = readTf(MODULE_DIR, 'outputs.tf');
const liveMain = readTf(LIVE_DIR, 'main.tf');
const liveOutputs = readTf(LIVE_DIR, 'outputs.tf');
const liveVariables = readTf(LIVE_DIR, 'variables.tf');

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

const APPLICATION_ROLES = [
  'admin',
  'organization_admin',
  'recruiter',
  'business_panel',
] as const;

const block = (source: string, type: string, name: string): string | undefined =>
  source.match(
    new RegExp(`resource "${type}" "${name}" \\{([\\s\\S]*?)\\n\\}`),
  )?.[1];

const moduleCall = (source: string, name: string): string | undefined =>
  source.match(new RegExp(`module "${name}" \\{([\\s\\S]*?)\\n\\}`))?.[1];

const variableBlock = (source: string, name: string): string | undefined =>
  source.match(new RegExp(`variable "${name}" \\{([\\s\\S]*?)\\n\\}`))?.[1];

const outputBlock = (source: string, name: string): string | undefined =>
  source.match(new RegExp(`output "${name}" \\{([\\s\\S]*?)\\n\\}`))?.[1];

describe('Entra Terraform application registration', () => {
  it('requests v2 access tokens for the protected API', () => {
    const apiApplication = block(moduleMain, 'azuread_application', 'api');

    expect(apiApplication).toBeDefined();
    expect(apiApplication).toMatch(/requested_access_token_version\s*=\s*2/);
  });

  it('restricts every registration to the single configured tenant', () => {
    for (const name of ['api', 'stack_a', 'stack_b']) {
      expect(block(moduleMain, 'azuread_application', name)).toMatch(
        /sign_in_audience\s*=\s*"AzureADMyOrg"/,
      );
    }
  });
});

describe('Entra Terraform create-or-reuse contract', () => {
  it('creates registrations only when reuse is disabled', () => {
    for (const name of ['api', 'stack_a', 'stack_b']) {
      expect(block(moduleMain, 'azuread_application', name)).toMatch(
        /count\s*=\s*var\.reuse \? 0 : 1/,
      );
      expect(block(moduleMain, 'azuread_service_principal', name)).toMatch(
        /count\s*=\s*var\.reuse \? 0 : 1/,
      );
    }
  });

  it('resolves every consumer-facing identifier through a reuse-aware local', () => {
    for (const local of [
      'api_app_client_id',
      'api_service_principal_object_id',
      'stack_a_client_id',
      'stack_b_client_id',
    ]) {
      expect(moduleMain).toMatch(
        new RegExp(`${local}\\s*=\\s*var\\.reuse \\? var\\.existing_`),
      );
    }
  });

  it('fails the plan when reuse is requested without every existing identifier', () => {
    const contract = block(moduleMain, 'terraform_data', 'reuse_contract');

    expect(contract).toBeDefined();
    expect(contract).toMatch(/precondition/);
    expect(contract).toMatch(/!var\.reuse \|\| alltrue\(/);

    for (const existing of [
      'existing_api_app_client_id',
      'existing_api_service_principal_object_id',
      'existing_stack_a_client_id',
      'existing_stack_b_client_id',
    ]) {
      expect(contract).toMatch(new RegExp(`var\\.${existing} != ""`));
    }
  });

  it('gates the browser-client service principals on reuse as well', () => {
    for (const name of ['stack_a', 'stack_b']) {
      expect(block(moduleMain, 'azuread_service_principal', name)).toMatch(
        /count\s*=\s*var\.reuse \? 0 : 1/,
      );
    }
  });

  it('never reads a counted registration from an output, which reuse would leave empty', () => {
    // local.* stays valid under reuse; azuread_application.api[0] would fail the plan.
    expect(moduleOutputs).not.toMatch(/azuread_application\./);
    expect(moduleOutputs).not.toMatch(/azuread_service_principal\./);
    expect(moduleOutputs).not.toMatch(/\[0\]/);
  });
});

describe('Entra Terraform stable role and scope identifiers', () => {
  it('declares the scope UUID as an operator-supplied variable rather than generating one', () => {
    expect(moduleVariables).toMatch(/variable "api_scope_id"/);
    expect(moduleMain).not.toMatch(/random_uuid/);

    const apiApplication = block(moduleMain, 'azuread_application', 'api');
    expect(apiApplication).toMatch(/id\s*=\s*var\.api_scope_id/);
    expect(apiApplication).toMatch(/value\s*=\s*var\.api_scope_value/);
  });

  it('binds every persisted application role to a stable supplied UUID', () => {
    const apiApplication = block(moduleMain, 'azuread_application', 'api') ?? '';

    for (const role of APPLICATION_ROLES) {
      expect(apiApplication).toMatch(
        new RegExp(`id\\s*=\\s*var\\.app_role_ids\\["${role}"\\]`),
      );
      expect(apiApplication).toMatch(new RegExp(`value\\s*=\\s*"${role}"`));
    }
  });

  it('exposes the stable role and scope identifiers to downstream configuration', () => {
    for (const output of ['app_role_ids', 'api_scope_id', 'api_scope']) {
      expect(moduleOutputs).toMatch(new RegExp(`output "${output}"`));
    }
  });

  it('defaults the scope and every role to a distinct literal UUID', () => {
    const scopeDefault = variableBlock(moduleVariables, 'api_scope_id')?.match(
      /default\s*=\s*"([^"]+)"/,
    )?.[1];

    expect(scopeDefault).toMatch(UUID);

    const roleDefaults = APPLICATION_ROLES.map(
      role =>
        variableBlock(moduleVariables, 'app_role_ids')?.match(
          new RegExp(`${role}\\s*=\\s*"([^"]+)"`),
        )?.[1],
    );

    for (const id of roleDefaults) {
      expect(id).toMatch(UUID);
    }
    expect(new Set([...roleDefaults, scopeDefault]).size).toBe(
      APPLICATION_ROLES.length + 1,
    );
  });

  it('rejects a role map that drops any persisted role value', () => {
    const roleIds = variableBlock(moduleVariables, 'app_role_ids') ?? '';

    expect(roleIds).toMatch(/validation\s*\{/);
    expect(roleIds).toMatch(/setsubtract\(/);
    for (const role of APPLICATION_ROLES) {
      expect(roleIds).toMatch(new RegExp(`"${role}"`));
    }
  });

  it('rejects role group names keyed by anything but a persisted role value', () => {
    const groupNames = variableBlock(moduleVariables, 'role_group_names') ?? '';

    expect(groupNames).toMatch(/validation\s*\{/);
    expect(groupNames).toMatch(/keys\(var\.role_group_names\)/);
    for (const role of APPLICATION_ROLES) {
      expect(groupNames).toMatch(new RegExp(`"${role}"`));
    }
  });
});

describe('Entra Terraform SPA secret posture', () => {
  it('never declares a password or certificate credential for any registration', () => {
    expect(moduleMain).not.toMatch(/azuread_application_password/);
    expect(moduleMain).not.toMatch(/azuread_application_certificate/);
    expect(moduleMain).not.toMatch(/azuread_service_principal_password/);
    expect(moduleMain).not.toMatch(/\bpassword\s*\{/);
    expect(moduleMain).not.toMatch(/client_secret/);
  });

  it('registers both browser clients as single-page applications', () => {
    for (const name of ['stack_a', 'stack_b']) {
      const application = block(moduleMain, 'azuread_application', name);

      expect(application).toMatch(/single_page_application\s*\{/);
      expect(application).not.toMatch(/\bweb\s*\{/);
      expect(application).not.toMatch(/public_client\s*\{/);
    }
  });

  it('requests only the delegated API scope for each browser client', () => {
    for (const name of ['stack_a', 'stack_b']) {
      const application = block(moduleMain, 'azuread_application', name) ?? '';

      expect(application).toMatch(
        /resource_app_id\s*=\s*local\.api_app_client_id/,
      );
      expect(application).toMatch(/id\s*=\s*var\.api_scope_id/);
      expect(application).toMatch(/type\s*=\s*"Scope"/);
      expect(application).not.toMatch(/type\s*=\s*"Role"/);
    }
  });

  it('accepts and emits no credential material anywhere in the module surface', () => {
    for (const source of [moduleVariables, moduleOutputs]) {
      expect(source).not.toMatch(/secret|password|certificate|thumbprint/i);
    }
    expect(moduleOutputs).not.toMatch(/sensitive\s*=\s*true/);
  });
});

describe('Entra Terraform redirect URIs', () => {
  it('drives every redirect URI from an explicit operator-supplied variable', () => {
    expect(block(moduleMain, 'azuread_application', 'stack_a')).toMatch(
      /redirect_uris\s*=\s*var\.stack_a_redirect_uris/,
    );
    expect(block(moduleMain, 'azuread_application', 'stack_b')).toMatch(
      /redirect_uris\s*=\s*var\.stack_b_redirect_uris/,
    );

    for (const variable of ['stack_a_redirect_uris', 'stack_b_redirect_uris']) {
      expect(moduleVariables).toMatch(new RegExp(`variable "${variable}"`));
    }
  });

  it('hardcodes no redirect URI in the module or the shared root', () => {
    expect(moduleMain).not.toMatch(/https?:\/\//);
    expect(moduleVariables).not.toMatch(/https?:\/\//);
    expect(liveMain).not.toMatch(/redirect_uris\s*=\s*\[/);
  });

  it('types both redirect URI inputs as an empty-by-default string list', () => {
    for (const variable of ['stack_a_redirect_uris', 'stack_b_redirect_uris']) {
      const declaration = variableBlock(moduleVariables, variable) ?? '';

      expect(declaration).toMatch(/type\s*=\s*list\(string\)/);
      expect(declaration).toMatch(/default\s*=\s*\[\]/);
    }
  });

  it('registers redirect URIs only through the single-page application block', () => {
    const redirectAssignments = [
      ...moduleMain.matchAll(/redirect_uris\s*=\s*(\S+)/g),
    ].map(([, value]) => value);

    expect(redirectAssignments).toEqual([
      'var.stack_a_redirect_uris',
      'var.stack_b_redirect_uris',
    ]);
  });
});

describe('Entra Terraform bootstrap admin assignment', () => {
  it('assigns the admin app role to the bootstrap identity when one is configured', () => {
    const assignment = block(
      moduleMain,
      'azuread_app_role_assignment',
      'bootstrap_admin',
    );

    expect(assignment).toBeDefined();
    expect(assignment).toMatch(
      /count\s*=\s*var\.bootstrap_admin_object_id != "" \? 1 : 0/,
    );
    expect(assignment).toMatch(/app_role_id\s*=\s*var\.app_role_ids\["admin"\]/);
    expect(assignment).toMatch(
      /principal_object_id\s*=\s*var\.bootstrap_admin_object_id/,
    );
    expect(assignment).toMatch(
      /resource_object_id\s*=\s*local\.api_service_principal_object_id/,
    );
  });

  it('assigns role groups against the same reuse-aware API service principal', () => {
    const assignment = block(
      moduleMain,
      'azuread_app_role_assignment',
      'role_group',
    );

    expect(assignment).toMatch(/for_each\s*=\s*azuread_group\.role/);
    expect(assignment).toMatch(
      /app_role_id\s*=\s*var\.app_role_ids\[each\.key\]/,
    );
    expect(assignment).toMatch(
      /resource_object_id\s*=\s*local\.api_service_principal_object_id/,
    );
  });

  it('creates security-enabled role groups only when explicitly requested', () => {
    expect(moduleMain).toMatch(
      /role_groups\s*=\s*var\.create_role_groups \? var\.role_group_names : \{\}/,
    );

    const group = block(moduleMain, 'azuread_group', 'role');
    expect(group).toMatch(/for_each\s*=\s*local\.role_groups/);
    expect(group).toMatch(/security_enabled\s*=\s*true/);
  });

  it('leaves the bootstrap assignment opt-in and grants no role beyond admin', () => {
    const declaration =
      variableBlock(moduleVariables, 'bootstrap_admin_object_id') ?? '';

    expect(declaration).toMatch(/default\s*=\s*""/);

    const appRoleIds = [
      ...moduleMain.matchAll(/app_role_id\s*=\s*(\S+)/g),
    ].map(([, value]) => value);

    expect(appRoleIds).toEqual([
      'var.app_role_ids[each.key]',
      'var.app_role_ids["admin"]',
    ]);
  });
});

describe('Entra Terraform shared-root wiring', () => {
  it('sources the Entra module from the shared foundation path', () => {
    const call = moduleCall(liveMain, 'entra');

    expect(call).toBeDefined();
    expect(call).toMatch(
      /source\s*=\s*"\.\.\/\.\.\/modules\/foundation\/entra"/,
    );
  });

  it('passes every reuse identifier through from the shared root', () => {
    const call = moduleCall(liveMain, 'entra') ?? '';

    expect(call).toMatch(/reuse\s*=\s*var\.reuse_entra/);
    expect(call).toMatch(
      /existing_api_app_client_id\s*=\s*var\.existing_entra_api_app_client_id/,
    );
    expect(call).toMatch(
      /existing_api_service_principal_object_id\s*=\s*var\.existing_entra_api_service_principal_object_id/,
    );
    expect(call).toMatch(
      /existing_stack_a_client_id\s*=\s*var\.existing_entra_stack_a_client_id/,
    );
    expect(call).toMatch(
      /existing_stack_b_client_id\s*=\s*var\.existing_entra_stack_b_client_id/,
    );
  });

  it('passes stable role, scope, redirect, group, and bootstrap inputs from the shared root', () => {
    const call = moduleCall(liveMain, 'entra') ?? '';

    expect(call).toMatch(
      /api_identifier_uri\s*=\s*var\.entra_api_identifier_uri/,
    );
    expect(call).toMatch(/api_scope_id\s*=\s*var\.entra_api_scope_id/);
    expect(call).toMatch(/api_scope_value\s*=\s*var\.entra_api_scope/);
    expect(call).toMatch(/app_role_ids\s*=\s*var\.entra_app_role_ids/);
    expect(call).toMatch(
      /stack_a_redirect_uris\s*=\s*var\.entra_stack_a_redirect_uris/,
    );
    expect(call).toMatch(
      /stack_b_redirect_uris\s*=\s*var\.entra_stack_b_redirect_uris/,
    );
    expect(call).toMatch(
      /create_role_groups\s*=\s*var\.entra_create_role_groups/,
    );
    expect(call).toMatch(/role_group_names\s*=\s*var\.entra_role_group_names/);
    expect(call).toMatch(
      /bootstrap_admin_object_id\s*=\s*var\.entra_bootstrap_admin_object_id/,
    );
  });

  it('re-exports every identifier the runtime configuration consumes', () => {
    for (const output of [
      'entra_api_app_client_id',
      'entra_api_service_principal_object_id',
      'entra_api_identifier_uri',
      'entra_api_scope',
      'entra_api_scope_id',
      'entra_stack_a_client_id',
      'entra_stack_b_client_id',
      'entra_app_role_ids',
      'entra_role_group_object_ids',
    ]) {
      expect(liveOutputs).toMatch(new RegExp(`output "${output}"`));
    }
  });

  it('declares every root variable the Entra module call consumes', () => {
    const call = moduleCall(liveMain, 'entra') ?? '';
    const referenced = new Set(
      [...call.matchAll(/var\.(\w+)/g)].map(([, name]) => name),
    );

    expect(referenced.size).toBeGreaterThan(0);
    for (const name of referenced) {
      expect(variableBlock(liveVariables, name)).toBeDefined();
    }
  });

  it('sources every re-exported Entra value from the module rather than a variable', () => {
    const entraOutputs = [
      ...liveOutputs.matchAll(/output "(entra_\w+)"/g),
    ].map(([, name]) => name);

    expect(entraOutputs.length).toBeGreaterThan(0);
    for (const name of entraOutputs) {
      expect(outputBlock(liveOutputs, name)).toMatch(
        /value\s*=\s*module\.entra\./,
      );
    }
  });

  it('exposes no client secret from the shared root', () => {
    expect(liveOutputs).not.toMatch(/client_secret|password/i);
  });
});
