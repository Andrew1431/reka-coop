# Installs a release package into another PC's Reka folder over its share, e.g.
#   mod/remote/install.ps1 -Target \\Olga\Reka -Zip local/release/RekaCoop-0.3.14.zip [-Wait]
# -Wait polls until the share is reachable (the PC is on). Reka must be closed there.
# Puts back BepInEx's generated interop from _RekaCoop_removed if present, so the first start is quick
# (BepInEx regenerates it anyway if the game was updated since).
param(
    [Parameter(Mandatory)][string]$Target,
    [Parameter(Mandatory)][string]$Zip,
    [switch]$Wait
)
$ErrorActionPreference = 'Stop'

while (-not (Test-Path -LiteralPath (Join-Path $Target 'Reka.exe'))) {
    if (-not $Wait) { throw "$Target is not reachable" }
    Start-Sleep -Seconds 30
}
Write-Host "$(Get-Date -Format T) $Target is reachable"

# A running game locks GameAssembly.dll.
while ($true) {
    try { [IO.File]::Open((Join-Path $Target 'GameAssembly.dll'), 'Open', 'ReadWrite', 'None').Close(); break }
    catch { if (-not $Wait) { throw 'Reka is running there; close it first' }; Write-Host 'Reka is running there; waiting'; Start-Sleep -Seconds 30 }
}

$tmp = Join-Path $env:TEMP "rekacoop-install-$([IO.Path]::GetFileNameWithoutExtension($Zip))"
if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
Expand-Archive -LiteralPath $Zip -DestinationPath $tmp
& robocopy $tmp $Target /E /NFL /NDL /NJH /NP /R:2 /W:5 | Out-Host
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
Remove-Item -LiteralPath $tmp -Recurse -Force

$removed = Join-Path $Target '_RekaCoop_removed\BepInEx'
foreach ($dir in 'interop', 'cache', 'unity-libs') {
    $from = Join-Path $removed $dir
    $to = Join-Path $Target "BepInEx\$dir"
    if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $to)) {
        & robocopy $from $to /E /NFL /NDL /NJH /NP | Out-Null
        Write-Host "Restored BepInEx\$dir"
    }
}
Write-Host "$(Get-Date -Format T) installed $(Split-Path $Zip -Leaf) to $Target"
exit 0  # robocopy leaves 1 ("files copied") in $LASTEXITCODE
