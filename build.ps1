<#
.SYNOPSIS
    Release build of the whole solution, then the Core tests: what "builds clean" means in docs/PLAN.md.
.DESCRIPTION
    Finds MSBuild with vswhere (it is rarely on the PATH outside a Developer prompt), runs
    `msbuild ClaudeVS.sln /restore /p:Configuration=Release /warnaserror`, then `dotnet test tests/ClaudeVS.Core.Tests`.
    Exits with the first failing step's exit code.
.PARAMETER NoTests
    Build only.
.EXAMPLE
    pwsh ./build.ps1
    powershell -ExecutionPolicy Bypass -File build.ps1 -NoTests
#>
param([switch]$NoTests)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Write-Error "vswhere not found at ${vswhere}: install Visual Studio 2022 or newer with the VS extension workload." }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { Write-Error 'No MSBuild found: install Visual Studio with the MSBuild component.' }

Write-Host "MSBuild: $msbuild"
& $msbuild ClaudeVS.sln /restore /p:Configuration=Release /warnaserror /verbosity:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Release build failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
Write-Host 'Release build: clean'

if (-not $NoTests) {
    & dotnet test tests/ClaudeVS.Core.Tests
    if ($LASTEXITCODE -ne 0) { Write-Host "Core tests failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
}
