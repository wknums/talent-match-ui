import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { delimiter, join } from 'node:path'
import { spawnSync } from 'node:child_process'
import { tmpdir } from 'node:os'
import { afterEach, describe, expect, it } from 'vitest'

const temporaryDirectories: string[] = []

afterEach(() => {
  for (const directory of temporaryDirectories.splice(0)) {
    rmSync(directory, { recursive: true, force: true })
  }
})

function runContextValidation(activeTenant: string, activeSubscription: string) {
  const directory = mkdtempSync(join(tmpdir(), 'talentmatch-azure-context-'))
  temporaryDirectories.push(directory)
  const commandLog = join(directory, 'az-commands.log')
  const azPath = join(directory, 'az')
  writeFileSync(azPath, `#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$MOCK_AZ_COMMAND_LOG"
if [[ "$*" == *"--query tenantId"* ]]; then
  printf '%s\\r\\n' "$MOCK_ACTIVE_TENANT"
elif [[ "$*" == *"--query id"* ]]; then
  printf '%s\\r\\n' "$MOCK_ACTIVE_SUBSCRIPTION"
else
  exit 64
fi
`)
  chmodSync(azPath, 0o755)

  const result = spawnSync('bash', ['-c', `source infra/scripts/lib/common.sh
AZURE_TENANT_ID="$EXPECTED_TENANT"
AZURE_SUBSCRIPTION_ID="$EXPECTED_SUBSCRIPTION"
validate_azure_context
`], {
    cwd: process.cwd(),
    encoding: 'utf8',
    env: {
      ...process.env,
      PATH: `${directory}${delimiter}${process.env.PATH}`,
      EXPECTED_TENANT: '11111111-1111-1111-1111-111111111111',
      EXPECTED_SUBSCRIPTION: '22222222-2222-2222-2222-222222222222',
      MOCK_ACTIVE_TENANT: activeTenant,
      MOCK_ACTIVE_SUBSCRIPTION: activeSubscription,
      MOCK_AZ_COMMAND_LOG: commandLog,
    },
  })

  return {
    ...result,
    commands: readFileSync(commandLog, 'utf8'),
  }
}

describe('Azure context validation', () => {
  it('accepts exact tenant and subscription IDs with Windows CRLF output', () => {
    const result = runContextValidation(
      '11111111-1111-1111-1111-111111111111',
      '22222222-2222-2222-2222-222222222222',
    )

    expect(result.status).toBe(0)
    expect(result.stdout).toContain('matches the environment profile')
  })

  it.each([
    ['tenant', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '22222222-2222-2222-2222-222222222222'],
    ['subscription', '11111111-1111-1111-1111-111111111111', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'],
  ])('rejects an exact %s mismatch without switching context', (_case, tenant, subscription) => {
    const result = runContextValidation(tenant, subscription)

    expect(result.status).not.toBe(0)
    expect(result.stderr).toContain('Refusing to continue')
    expect(result.commands).not.toMatch(/account set/i)
  })
})