<#
.SYNOPSIS
  Publishes a built ClaudeVS .vsix to the Visual Studio Marketplace with VsixPublisher.exe.

.DESCRIPTION
  Used by the "Publish to Marketplace" workflow, and works the same way locally:

    ./marketplace/publish.ps1 -Vsix src/ClaudeVS/bin/Release/ClaudeVS.vsix -Publisher <id> -Pat <token>

  Add -DryRun to check the inputs and find VsixPublisher.exe without publishing.

  The publisher ID goes into a temporary copy of publishManifest.json; the file in the repo keeps its placeholder.
  VsixPublisher.exe comes from the Microsoft.VSSDK.BuildTools package, using the version the extension project
  references: from the local NuGet cache, otherwise downloaded from nuget.org.
#>
param(
    [Parameter(Mandatory)] [string] $Vsix,
    [Parameter(Mandatory)] [string] $Publisher,
    [string] $Pat = $env:VS_MARKETPLACE_PAT,
    [string] $IgnoreWarnings = '',
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not (Test-Path $Vsix)) { throw "VSIX not found: $Vsix" }
if ($Publisher -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]*$') { throw "Publisher ID '$Publisher' doesn't look like a Marketplace publisher ID (letters, digits, - and _)." }
if (-not $DryRun -and -not $Pat) { throw 'No personal access token: pass -Pat or set VS_MARKETPLACE_PAT.' }

# VsixPublisher.exe from the same Microsoft.VSSDK.BuildTools version the project builds with.
[xml]$proj = Get-Content (Join-Path $root 'src/ClaudeVS/ClaudeVS.csproj')
$sdkVersion = ($proj.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'Microsoft.VSSDK.BuildTools' }).Version
if (-not $sdkVersion) { throw 'Microsoft.VSSDK.BuildTools is not referenced in ClaudeVS.csproj.' }

$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$publisherExe = Join-Path $nugetRoot "microsoft.vssdk.buildtools/$sdkVersion/tools/vssdk/bin/VsixPublisher.exe"
if (-not (Test-Path $publisherExe)) {
    $tools = Join-Path ([IO.Path]::GetTempPath()) "claudevs-vssdk-$sdkVersion"
    $publisherExe = Join-Path $tools 'tools/vssdk/bin/VsixPublisher.exe'
    if (-not (Test-Path $publisherExe)) {
        $nupkg = "$tools.zip"
        "Downloading Microsoft.VSSDK.BuildTools $sdkVersion from nuget.org…"
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.vssdk.buildtools/$sdkVersion/microsoft.vssdk.buildtools.$sdkVersion.nupkg" -OutFile $nupkg
        Expand-Archive $nupkg -DestinationPath $tools -Force
        Remove-Item $nupkg
    }
}
if (-not (Test-Path $publisherExe)) { throw "VsixPublisher.exe not found at $publisherExe" }

# Temporary manifest next to the original, so its relative "overview" path still resolves.
$manifest = Get-Content (Join-Path $PSScriptRoot 'publishManifest.json') -Raw | ConvertFrom-Json
$manifest.publisher = $Publisher
$tempManifest = Join-Path $PSScriptRoot 'publishManifest.generated.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content $tempManifest -Encoding utf8

try {
    "VSIX:            $Vsix"
    "Publisher:       $Publisher"
    "VsixPublisher:   $publisherExe"
    if ($DryRun) { 'Dry run: not publishing.'; return }

    $publishArgs = @('publish', '-payload', (Resolve-Path $Vsix).Path, '-publishManifest', $tempManifest, '-personalAccessToken', $Pat)
    if ($IgnoreWarnings) { $publishArgs += @('-ignoreWarnings', $IgnoreWarnings) }
    & $publisherExe @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "VsixPublisher.exe failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item $tempManifest -ErrorAction SilentlyContinue
}
