import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { beforeAll, describe, expect, it } from 'vitest'

const fixturePath = resolve(process.cwd(), 'tests/fixtures/authorization-parity-cases.json')
const runnerModuleUrl = new URL('./run-auth-parity.js', import.meta.url).href

const requiredAreas = {
  auth: ['authorization', 'pending-profile', 'authorization-denial', 'explicit-default'],
  'access-management': ['access-management'],
  'organization-administration': ['organization-administration'],
  jobs: ['job-authorization', 'job-validation'],
  revocation: ['revocation'],
  'simple-mode-isolation': ['mode-isolation'],
} as const

type AuthenticationMode = 'entra' | 'simple'
type ParityArea = keyof typeof requiredAreas

interface CanonicalParityResult {
  status: number
  [field: string]: unknown
}

interface AuthorizationParityCase {
  id: string
  category: string
  operation?: {
    kind: string
    method?: string
    path?: string
    body?: Record<string, unknown>
    [field: string]: unknown
  }
  operationFromCase?: string
  expectedByMode: Record<AuthenticationMode, CanonicalParityResult>
  [field: string]: unknown
}

interface AuthorizationParityFixture {
  schemaVersion: number
  cases: AuthorizationParityCase[]
  [field: string]: unknown
}

interface AuthorizationParityRunnerModule {
  executeStackAParityCase(
    fixture: AuthorizationParityFixture,
    testCase: AuthorizationParityCase,
    mode: AuthenticationMode,
  ): Promise<unknown>
  canonicalizeAuthorizationParityResult(result: unknown): CanonicalParityResult
}

function loadFixture(): AuthorizationParityFixture {
  if (!existsSync(fixturePath)) {
    throw new Error(
      'T075 prerequisite missing: tests/fixtures/authorization-parity-cases.json must define the shared parity cases.',
    )
  }

  const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as Partial<AuthorizationParityFixture>
  if (!Array.isArray(fixture.cases)) {
    throw new Error('Authorization parity fixture must contain a top-level cases array.')
  }

  if (fixture.schemaVersion !== 1) {
    throw new Error(`Unsupported authorization parity fixture schemaVersion: ${String(fixture.schemaVersion)}.`)
  }

  for (const testCase of fixture.cases) {
    if (!testCase?.id || !testCase.category || (!testCase.operation && !testCase.operationFromCase)
      || !testCase.expectedByMode?.entra || !testCase.expectedByMode?.simple) {
      throw new Error(
        'Each authorization parity case must define id, category, an operation or operationFromCase, and both mode expectations.',
      )
    }
  }

  return fixture as AuthorizationParityFixture
}

async function loadRunner(): Promise<AuthorizationParityRunnerModule> {
  let module: Partial<AuthorizationParityRunnerModule>
  try {
    module = await import(/* @vite-ignore */ runnerModuleUrl) as Partial<AuthorizationParityRunnerModule>
  } catch (error) {
    throw new Error(
      'T081 prerequisite missing: tests/integration/run-auth-parity.ts must export Stack A execution and canonicalization.',
      { cause: error },
    )
  }

  if (typeof module.executeStackAParityCase !== 'function'
    || typeof module.canonicalizeAuthorizationParityResult !== 'function') {
    throw new Error(
      'Parity runner must export executeStackAParityCase and canonicalizeAuthorizationParityResult.',
    )
  }

  return module as AuthorizationParityRunnerModule
}

function casesForArea(fixture: AuthorizationParityFixture, area: ParityArea): AuthorizationParityCase[] {
  const categories: readonly string[] = requiredAreas[area]
  return fixture.cases.filter(testCase => categories.includes(testCase.category))
}

describe('Stack A authorization parity', () => {
  let fixture: AuthorizationParityFixture

  beforeAll(() => {
    fixture = loadFixture()
  })

  it('covers every required Stack A authorization surface', () => {
    for (const area of Object.keys(requiredAreas) as ParityArea[]) {
      expect(casesForArea(fixture, area), `Fixture must include at least one ${area} case.`)
        .not.toHaveLength(0)
    }
  })

  it.each(Object.keys(requiredAreas) as ParityArea[])('executes and canonicalizes %s cases', async area => {
    const testCases = casesForArea(fixture, area)
    const runner = await loadRunner()
    const modes: AuthenticationMode[] = area === 'simple-mode-isolation' ? ['entra', 'simple'] : ['entra']

    for (const testCase of testCases) {
      for (const mode of modes) {
        const rawResult = await runner.executeStackAParityCase(fixture, testCase, mode)
        const canonicalResult = runner.canonicalizeAuthorizationParityResult(rawResult)

        expect(canonicalResult, `${testCase.id} (${mode})`).toEqual(testCase.expectedByMode[mode])
      }
    }
  }, 30_000)
})