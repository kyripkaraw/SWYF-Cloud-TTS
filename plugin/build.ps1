<#
.SYNOPSIS
    Builds the in-game settings plugin and its Unity Doorstop bootstrap.

.DESCRIPTION
    Produces:
      plugin\dist\SwyfCloudTts.Plugin.dll   - the settings card (Settings > Speech)
      plugin\dist\SwyfCloudTts.Boot.dll     - Doorstop entrypoint that hosts the plugin

    Both compile against the game's own assemblies (see Directory.Build.targets), found in the
    default Steam library unless -GameDir points elsewhere:
      .\plugin\build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Scam With Your Friends"

.EXAMPLE
    .\plugin\build.ps1
#>

[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projects = @(
    @{ Name = 'SwyfCloudTts.Plugin'; Path = Join-Path $PSScriptRoot 'SwyfCloudTts.Plugin\SwyfCloudTts.Plugin.csproj' },
    @{ Name = 'SwyfCloudTts.Boot'; Path = Join-Path $PSScriptRoot 'SwyfCloudTts.Boot\SwyfCloudTts.Boot.csproj' }
)
$stage = Join-Path $PSScriptRoot 'dist'

New-Item -ItemType Directory -Force -Path $stage | Out-Null

foreach ($project in $projects) {
    $arguments = @('build', $project.Path, '-c', $Configuration, '--nologo')
    if ($GameDir) {
        $arguments += "/p:GameDir=$GameDir"
    }

    Write-Host "==> dotnet $($arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $dll = Join-Path $PSScriptRoot "$($project.Name)\bin\$Configuration\net472\$($project.Name).dll"
    if (-not (Test-Path $dll)) {
        throw "Build finished but the assembly is missing: $dll"
    }

    Copy-Item -LiteralPath $dll -Destination (Join-Path $stage "$($project.Name).dll") -Force

    $pdb = [System.IO.Path]::ChangeExtension($dll, '.pdb')
    if (Test-Path $pdb) {
        Copy-Item -LiteralPath $pdb -Destination (Join-Path $stage "$($project.Name).pdb") -Force
    }

    $size = [math]::Round((Get-Item $dll).Length / 1KB, 1)
    Write-Host "==> Staged $stage\$($project.Name).dll ($size KB)" -ForegroundColor Green
}
