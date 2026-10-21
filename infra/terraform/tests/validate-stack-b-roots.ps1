[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$testDirectory = Split-Path -Parent $PSCommandPath
$terraformDirectory = [System.IO.Path]::GetFullPath((Join-Path $testDirectory ".."))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $terraformDirectory "..\.."))
$liveDirectory = Join-Path $terraformDirectory "live"
$requiredSharedRoot = [System.IO.Path]::GetFullPath((Join-Path $liveDirectory "shared"))
$requiredStackBRoot = [System.IO.Path]::GetFullPath((Join-Path $liveDirectory "stack-b"))

function Get-RepoRelativePath([string] $Path) {
    return [System.IO.Path]::GetRelativePath($repoRoot, $Path).Replace("\", "/")
}

function Get-RootTerraformText([string] $Directory) {
    return (Get-ChildItem -LiteralPath $Directory -File -Filter "*.tf" |
        Sort-Object -Property Name |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
}

function Test-IsStackBRoot([System.IO.DirectoryInfo] $Directory) {
    $relative = [System.IO.Path]::GetRelativePath($liveDirectory, $Directory.FullName).Replace("\", "/")
    if (($relative -split "/") -contains "stack-b") {
        return $true
    }

    $terraformText = Get-RootTerraformText $Directory.FullName
    return $terraformText -match '(?m)^\s*module\s+"stack_b"\s*\{' -or
        $terraformText -match '(?m)^\s*source\s*=\s*"[^"]*modules/stack-b"' -or
        $terraformText -match '(?m)^\s*stack\s*=\s*"stack-b"\s*$'
}

$missingRoots = @($requiredSharedRoot, $requiredStackBRoot) |
    Where-Object { -not (Test-Path -LiteralPath $_ -PathType Container) }
if ($missingRoots.Count -gt 0) {
    foreach ($missingRoot in $missingRoots) {
        Write-Error "Required Terraform root is absent: $(Get-RepoRelativePath $missingRoot)"
    }
    exit 1
}

$candidateRoots = Get-ChildItem -LiteralPath $liveDirectory -Directory -Recurse |
    Where-Object {
        $_.FullName -notmatch '[\\/]\.terraform(?:[\\/]|$)' -and
        (Get-ChildItem -LiteralPath $_.FullName -File -Filter "*.tf").Count -gt 0
    } |
    Sort-Object -Property @{ Expression = { Get-RepoRelativePath $_.FullName } }

$stackBRoots = @($candidateRoots | Where-Object { Test-IsStackBRoot $_ })
if ($stackBRoots.FullName -notcontains $requiredStackBRoot) {
    Write-Error "Required Stack B Terraform root was not classified: $(Get-RepoRelativePath $requiredStackBRoot)"
    exit 1
}

$rootsToValidate = @(
    [pscustomobject]@{ Classification = "shared"; Directory = $requiredSharedRoot }
    $stackBRoots | ForEach-Object {
        [pscustomobject]@{ Classification = "stack-b"; Directory = $_.FullName }
    }
) | Sort-Object -Property @{ Expression = { Get-RepoRelativePath $_.Directory } } -Unique

Write-Host "Terraform roots selected deterministically:"
foreach ($root in $rootsToValidate) {
    Write-Host "  [$($root.Classification)] $(Get-RepoRelativePath $root.Directory)"
}

$terraformCommand = Get-Command terraform -ErrorAction SilentlyContinue
if ($null -eq $terraformCommand) {
    Write-Error "terraform is required to validate the discovered roots."
    exit 1
}

$failed = @()
foreach ($root in $rootsToValidate) {
    $relative = Get-RepoRelativePath $root.Directory
    Write-Host "`n==> terraform -chdir=$relative validate"
    & $terraformCommand.Source "-chdir=$($root.Directory)" validate
    if ($LASTEXITCODE -ne 0) {
        $failed += $relative
    }
}

if ($failed.Count -gt 0) {
    Write-Error "Terraform validation failed for: $($failed -join ', ')"
    exit 1
}

Write-Host "`nPASS: shared and all discovered Stack B Terraform roots validated."
