<#
.SYNOPSIS
    Builds everything and stages the release file players download.

.DESCRIPTION
    Produces artifacts\SWYF-CloudTTS-Setup-v<version>.exe and its .sha256 file.
    The version comes from Directory.Build.props. The plugin compiles against the installed
    game, so run this on a machine that has it (CI cannot).

.EXAMPLE
    .\release.ps1
#>

[CmdletBinding()]
param(
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'

$buildArgs = @{}
if ($GameDir) { $buildArgs.GameDir = $GameDir }
& (Join-Path $PSScriptRoot 'build.ps1') @buildArgs

$dist = Join-Path $PSScriptRoot 'dist'
$worker = Join-Path $dist 'QwenTtsWorker.exe'
$setup = Join-Path $dist 'SWYF-CloudTTS-Setup.exe'

$version = ([xml](Get-Content -Raw (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
$workerVersion = & $worker --version
$setupVersion = (Get-Item $setup).VersionInfo.FileVersion
if ($workerVersion -ne $version -or -not $setupVersion.StartsWith("$version.")) {
    throw "Version mismatch: props=$version worker=$workerVersion setup=$setupVersion"
}

Write-Host '==> Smoke test: mock provider' -ForegroundColor Cyan
& $worker --test --provider mock --text 'release smoke test' --out (Join-Path $env:TEMP 'swyf-release-smoke.wav') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'worker smoke test failed' }

$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$target = Join-Path $artifacts "SWYF-CloudTTS-Setup-v$version.exe"
Copy-Item -LiteralPath $setup -Destination $target -Force

$hash = (Get-FileHash -Algorithm SHA256 $target).Hash.ToLowerInvariant()
Set-Content -Path "$target.sha256" -Value "$hash  $(Split-Path $target -Leaf)" -Encoding ascii

$size = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "==> $target ($size MB)" -ForegroundColor Green
Write-Host "    sha256 $hash"
