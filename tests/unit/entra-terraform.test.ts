import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

describe('Entra Terraform application registration', () => {
  it('requests v2 access tokens for the protected API', () => {
    const source = readFileSync(
      resolve('infra/terraform/modules/foundation/entra/main.tf'),
      'utf8',
    );
    const apiApplication = source.match(
      /resource "azuread_application" "api" \{([\s\S]*?)\n\}/,
    )?.[1];

    expect(apiApplication).toBeDefined();
    expect(apiApplication).toMatch(/requested_access_token_version\s*=\s*2/);
  });
});