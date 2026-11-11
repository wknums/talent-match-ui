import fs from 'node:fs'
import path from 'node:path'

export const repoRoot = path.resolve(__dirname, '..')
export const dynamicRubricFeatureDir = path.join(repoRoot, 'specs', '001-dynamic-rubric-editor')
export const dynamicRubricFixturesDir = path.join(dynamicRubricFeatureDir, 'contracts', 'fixtures')
export const dynamicRubricContractsDir = path.join(dynamicRubricFeatureDir, 'contracts')

export function dynamicRubricFixturePath(fileName: string): string {
  return path.join(dynamicRubricFixturesDir, fileName)
}

export function dynamicRubricContractPath(fileName: string): string {
  return path.join(dynamicRubricContractsDir, fileName)
}

export function loadDynamicRubricFixtureText(fileName: string): string {
  return fs.readFileSync(dynamicRubricFixturePath(fileName), 'utf8')
}

export function loadDynamicRubricFixtureJson<T>(fileName: string): T {
  return JSON.parse(loadDynamicRubricFixtureText(fileName)) as T
}

export function loadDynamicRubricContractJson<T>(fileName: string): T {
  return JSON.parse(fs.readFileSync(dynamicRubricContractPath(fileName), 'utf8')) as T
}

export function canonicalJson(value: unknown): string {
  return JSON.stringify(value, null, 2)
}

export function ensureDynamicRubricArtifactExists(relativePath: string): void {
  const fullPath = path.join(dynamicRubricFeatureDir, relativePath)
  if (!fs.existsSync(fullPath)) {
    throw new Error(`Expected dynamic rubric feature artifact to exist: ${fullPath}`)
  }
}
