[CmdletBinding()]
param(
    # Test copies to install into (folders under ..\game). Default: every copy.
    [string[]]$Instances,
    # Leave out dev-only code. Never ship a build without it.
    [switch]$Publish,
    # Only the local copies, not the PCs in ..\local\remote-targets.txt.
    [switch]$NoRemote
)

$ErrorActionPreference = 'Stop'

$gameRoot = Join-Path $PSScriptRoot '..\game' | Resolve-Path
if (-not $Instances) { $Instances = Get-ChildItem -LiteralPath $gameRoot -Directory | Select-Object -ExpandProperty Name }

# Build against the play copy's BepInEx (kept on the current game build; see sync-game.ps1), else the first copy's.
$buildFrom = if (Test-Path -LiteralPath (Join-Path $gameRoot 'play\BepInEx\interop\Assembly-CSharp.dll')) { 'play' } else { $Instances[0] }
$bepInEx = Join-Path $gameRoot "$buildFrom\BepInEx"
$interop = Join-Path $bepInEx 'interop'
if (-not (Test-Path -LiteralPath (Join-Path $interop 'Assembly-CSharp.dll'))) {
    throw "No interop at $interop. Launch that copy once with BepInEx to generate it."
}

$arguments = @(
    'build', (Join-Path $PSScriptRoot 'RekaCoop\RekaCoop.csproj'),
    '--configuration', 'Release',
    "--property:BepInExCoreDir=$(Join-Path $bepInEx 'core')",
    "--property:InteropDir=$interop"
)
if (-not $Publish) { $arguments += '--property:DevBuild=true' }
else { $arguments += '--property:DebugType=none' }  # no local paths baked into the shipped DLL
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot 'RekaCoop\bin\Release\net6.0\RekaCoop.dll'
# Other PCs' game folders (UNC paths, one per line) in ..\local\remote-targets.txt; their game must be closed.
$remoteList = Join-Path $PSScriptRoot '..\local\remote-targets.txt'
$remotes = if (-not $NoRemote -and (Test-Path -LiteralPath $remoteList)) { Get-Content -LiteralPath $remoteList | Where-Object { $_ -and $_ -notmatch '^\s*#' } } else { @() }
$targets = @($Instances | ForEach-Object { Join-Path $gameRoot $_ }) + @($remotes)
foreach ($root in $targets) {
    $dest = Join-Path $root 'BepInEx\plugins\RekaCoop'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    try {
        Copy-Item -LiteralPath $dll -Destination $dest -Force
        $pdb = [IO.Path]::ChangeExtension($dll, '.pdb')
        if (-not $Publish -and (Test-Path -LiteralPath $pdb)) { Copy-Item -LiteralPath $pdb -Destination $dest -Force }
        Copy-Item -LiteralPath (Join-Path (Split-Path $dll) 'LiteNetLib.dll') -Destination $dest -Force
        Write-Host "Installed to $dest"
    }
    catch {
        Write-Warning "Could not install to $dest (is that copy running?): $($_.Exception.Message)"
    }
}
