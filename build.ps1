<#
.SYNOPSIS
    Builds the worker, the in-game plugin and the installer.

.DESCRIPTION
    Output:
      dist\QwenTtsWorker.exe          cloud worker (self-contained .NET 8, single file)
      plugin\dist\SwyfCloudTts.*.dll  in-game settings card and its Doorstop entrypoint
      dist\SWYF-CloudTTS-Setup.exe    installer with all of the above embedded

    The plugin compiles against the game's own assemblies, so the game must be installed.
    Pass -GameDir when it is not in the default Steam library.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Scam With Your Friends"
#>

[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'

function Invoke-Dotnet([string[]]$arguments) {
    Write-Host "==> dotnet $($arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) failed with exit code $LASTEXITCODE" }
}

Invoke-Dotnet @('publish', (Join-Path $PSScriptRoot 'src\QwenTtsCloud\QwenTtsCloud.csproj'),
    '-c', $Configuration, '-o', $dist, '--nologo')

$pluginArgs = @{ Configuration = $Configuration }
if ($GameDir) { $pluginArgs.GameDir = $GameDir }
& (Join-Path $PSScriptRoot 'plugin\build.ps1') @pluginArgs

$installer = Join-Path $PSScriptRoot 'installer\SwyfCloudTts.Setup\SwyfCloudTts.Setup.csproj'
Invoke-Dotnet @('build', $installer, '-c', $Configuration, '--nologo')
$setup = Join-Path $PSScriptRoot "installer\SwyfCloudTts.Setup\bin\$Configuration\net48\SWYF-CloudTTS-Setup.exe"
Copy-Item -LiteralPath $setup -Destination $dist -Force

foreach ($file in 'QwenTtsWorker.exe', 'SWYF-CloudTTS-Setup.exe') {
    $size = [math]::Round((Get-Item (Join-Path $dist $file)).Length / 1MB, 1)
    Write-Host "==> dist\$file ($size MB)" -ForegroundColor Green
}
