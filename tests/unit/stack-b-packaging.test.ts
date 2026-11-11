import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { describe, expect, it } from 'vitest'

const read = (path: string) => readFileSync(resolve(path), 'utf8')

describe('Stack B dependency security', () => {
  it('pins local and CI builds to a patched stable SDK', () => {
    const global = JSON.parse(read('global.json'))
    expect(global.sdk).toEqual({
      version: '10.0.402',
      rollForward: 'latestPatch',
      allowPrerelease: false,
    })
    expect(read('.github/workflows/selective-azure-deploy.yml'))
      .toContain('global-json-file: global.json')
  })

  it('audits every dependency and fails on advisories or unavailable audit data', () => {
    const props = read('dotnet/Directory.Build.props')
    expect(read('NuGet.Config')).toContain('<auditSources>')
    expect(read('NuGet.Config')).toContain('value="https://data.nuget.org/v3/index.json"')
    expect(props).toContain('<NuGetAudit>true</NuGetAudit>')
    expect(props).toContain('<NuGetAuditMode>all</NuGetAuditMode>')
    expect(props).toContain('<NuGetAuditLevel>low</NuGetAuditLevel>')
    const warnings = /<WarningsAsErrors>([^<]+)<\/WarningsAsErrors>/.exec(props)?.[1]
    for (const code of ['NU1900', 'NU1901', 'NU1902', 'NU1903', 'NU1904', 'NU1905']) {
      expect(warnings?.split(';')).toContain(code)
    }
  })

  it('aligns Microsoft .NET 10 package references to the shared patched version', () => {
    expect(read('dotnet/Directory.Build.props'))
      .toContain('<DotNetPatchVersion>10.0.13</DotNetPatchVersion>')
    for (const project of [
      'src/Application/TalentMatch.Application.csproj',
      'src/Infrastructure/TalentMatch.Infrastructure.csproj',
      'src/Web.Client/TalentMatch.Web.Client.csproj',
      'src/Web.Server/TalentMatch.Web.Server.csproj',
      'tests/Infrastructure.Tests/TalentMatch.Infrastructure.Tests.csproj',
      'tests/Web.Tests/TalentMatch.Web.Tests.csproj',
    ]) {
      expect(read(`dotnet/${project}`)).not.toMatch(/Version="10\.0\.\d+"/)
      expect(read(`dotnet/${project}`)).toContain('Version="$(DotNetPatchVersion)"')
    }
  })

  it('overrides the vulnerable transitive PKCS dependency at its infrastructure root', () => {
    expect(read('dotnet/src/Infrastructure/TalentMatch.Infrastructure.csproj'))
      .toContain('Include="System.Security.Cryptography.Pkcs" Version="$(DotNetPatchVersion)"')
  })

  it('pins the runtime patch without changing ordinary framework-dependent builds', () => {
    const project = read('dotnet/src/Web.Server/TalentMatch.Web.Server.csproj')
    expect(project).toContain('<RuntimeFrameworkVersion>$(DotNetPatchVersion)</RuntimeFrameworkVersion>')
    expect(project).toContain('<RollForward>LatestPatch</RollForward>')
    expect(project).toContain('<PropertyGroup Condition="\'$(PackageStackB)\' == \'true\'">')
  })

  it('performs a fresh solution audit before publishing without a second restore', () => {
    const script = read('infra/scripts/package-stack-b.sh')
    const restore = script.indexOf('dotnet restore "$SOLUTION_PATH" --force')
    const publish = script.indexOf('dotnet publish "$PROJECT_PATH"')
    expect(restore).toBeGreaterThanOrEqual(0)
    expect(publish).toBeGreaterThan(restore)
    expect(script.slice(publish)).toContain('--no-restore')
    expect(script).not.toContain('NuGetAudit=false')
  })

  it('restores Release runtime assets before publishing a self-contained Linux x64 application', () => {
    const script = read('infra/scripts/package-stack-b.sh')
    const restore = script.indexOf('dotnet restore "$PROJECT_PATH" -p:PackageStackB=true -p:Configuration=Release')
    const publish = script.indexOf('dotnet publish "$PROJECT_PATH"')
    expect(restore).toBeGreaterThan(script.indexOf('dotnet restore "$SOLUTION_PATH" --force'))
    expect(publish).toBeGreaterThan(restore)
    expect(script.slice(publish)).toContain('-p:PackageStackB=true')
    expect(script).not.toContain('--self-contained false')
  })

  it('rejects a publish output without the bundled runtime before creating the ZIP', () => {
    const script = read('infra/scripts/package-stack-b.sh')
    const validation = script.indexOf('for runtime_file in')
    expect(validation).toBeGreaterThan(script.indexOf('dotnet publish "$PROJECT_PATH"'))
    expect(script.indexOf('log_info "Creating zip artifact..."')).toBeGreaterThan(validation)
    for (const file of [
      'libcoreclr.so',
      'libhostfxr.so',
      'libhostpolicy.so',
      'System.Private.CoreLib.dll',
      'Microsoft.AspNetCore.dll',
    ]) {
      expect(script).toContain(file)
    }
    expect(script).toContain('includedFrameworks')
    expect(script).toContain('log_fatal')
  })

  it('keeps the browser client isolated from the server runtime publish settings', () => {
    const project = read('dotnet/src/Web.Server/TalentMatch.Web.Server.csproj')
    expect(project).toMatch(
      /<PropertyGroup Condition="'\$\(PackageStackB\)' == 'true'">\s*<RuntimeIdentifier>linux-x64<\/RuntimeIdentifier>\s*<SelfContained>true<\/SelfContained>\s*<\/PropertyGroup>/,
    )
    const script = read('infra/scripts/package-stack-b.sh')
    expect(script).not.toContain('--runtime')
    expect(script).not.toContain('--self-contained true')
  })

  it('runs solution tests before uploading the deployment artifact', () => {
    const workflow = read('.github/workflows/selective-azure-deploy.yml')
    const job = workflow.slice(
      workflow.indexOf('  package-stack-b:'),
      workflow.indexOf('  deploy-stack-a:'),
    )
    const tests = job.indexOf('dotnet test dotnet/TalentMatch.slnx --configuration Release --no-restore')
    expect(tests).toBeGreaterThanOrEqual(0)
    expect(job.indexOf('uses: actions/upload-artifact')).toBeGreaterThan(tests)
  })
})
