<#
.SYNOPSIS
    Builds SWYF Cloud TTS as a single BepInEx 5 plugin and packs the release zip for Switchboard.

.DESCRIPTION
    Produces:
      plugin\dist-bepinex\SwyfCloudTts.dll                    - the whole mod: cloud voices and the settings card
      artifacts\SWYF-CloudTTS-BepInEx-vX.Y.Z.zip              - that dll at the top of a zip, as Switchboard expects

    Unity and Newtonsoft are referenced from the game install, found in the default Steam library
    unless -GameDir points elsewhere. -Install also copies the dll into the game's BepInEx\plugins.

.EXAMPLE
    .\plugin\build-bepinex.ps1 -Install
#>

[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Configuration = 'Release',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'SwyfCloudTts.BepInEx\SwyfCloudTts.BepInEx.csproj'
$stage = Join-Path $PSScriptRoot 'dist-bepinex'
$artifacts = Join-Path $root 'artifacts'

$arguments = @('build', $project, '-c', $Configuration, '--nologo')
if ($GameDir) {
    $arguments += "/p:GameDir=$GameDir"
}

Write-Host "==> dotnet $($arguments -join ' ')" -ForegroundColor Cyan
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$dll = Join-Path $PSScriptRoot "SwyfCloudTts.BepInEx\bin\$Configuration\net472\SwyfCloudTts.dll"
if (-not (Test-Path $dll)) {
    throw "Build finished but the assembly is missing: $dll"
}

New-Item -ItemType Directory -Force -Path $stage, $artifacts | Out-Null
Copy-Item -LiteralPath $dll -Destination $stage -Force

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$zip = Join-Path $artifacts "SWYF-CloudTTS-BepInEx-v$version.zip"
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}

# Switchboard installs the files at the top of the zip; nothing else may be in it.
Compress-Archive -LiteralPath (Join-Path $stage 'SwyfCloudTts.dll') -DestinationPath $zip
$size = [math]::Round((Get-Item $dll).Length / 1KB, 1)
Write-Host "==> $zip (SwyfCloudTts.dll, $size KB)" -ForegroundColor Green

if ($Install) {
    if (-not $GameDir) {
        $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Scam With Your Friends'
    }

    $plugins = Join-Path $GameDir 'BepInEx\plugins'
    if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core'))) {
        throw "BepInEx is not installed in $GameDir; install it with Switchboard first."
    }

    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item -LiteralPath $dll -Destination $plugins -Force
    Write-Host "==> Installed to $plugins" -ForegroundColor Green
}
