[CmdletBinding()]
param(
    [string]$Version = '0.4.0'
)

# Builds the player package: pinned BepInEx + the plugin (without dev code) + install notes, as one zip.
$ErrorActionPreference = 'Stop'
$workspace = Join-Path $PSScriptRoot '..' | Resolve-Path
$bepZip = Join-Path $workspace 'vendor\bepinex-be791\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791+b782da4.zip'
$out = Join-Path $workspace 'local\release'
$stage = Join-Path $out "RekaCoop-$Version"

& (Join-Path $PSScriptRoot 'build.ps1') -Publish -Instances host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$bin = Join-Path $PSScriptRoot 'RekaCoop\bin\Release\net6.0'

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Expand-Archive -LiteralPath $bepZip -DestinationPath $stage
$plugin = Join-Path $stage 'BepInEx\plugins\RekaCoop'
New-Item -ItemType Directory -Force -Path $plugin | Out-Null
Copy-Item -LiteralPath (Join-Path $bin 'RekaCoop.dll'), (Join-Path $bin 'LiteNetLib.dll') -Destination $plugin
# Game build 26298851 logs from threads BepInEx's Unity log listener can't take (coreclr crash right after Steam init).
# BepInEx fills in the rest of the file with defaults on first start.
$config = Join-Path $stage 'BepInEx\config'
New-Item -ItemType Directory -Force -Path $config | Out-Null
Set-Content -LiteralPath (Join-Path $config 'BepInEx.cfg') -Encoding ascii -Value "[Logging]`r`n`r`nUnityLogListening = false`r`n"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-players.txt') -Destination (Join-Path $stage 'README-RekaCoop.txt')
Copy-Item -Path (Join-Path $PSScriptRoot 'dist\*.bat') -Destination $stage

$zip = Join-Path $out "RekaCoop-$Version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Package: $zip"
if (Select-String -LiteralPath (Join-Path $PSScriptRoot 'RekaCoop\Diagnostics\Reporter.cs') -Pattern 'DefaultEndpoint = "";' -Quiet) {
    Write-Warning 'Reporter.DefaultEndpoint is empty: players'' log reports will wait on their disks (server/log-reporter is not deployed).'
}

# The dev copies keep the dev build.
& (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
