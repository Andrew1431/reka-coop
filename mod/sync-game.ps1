# Brings the hardlinked test copies under ..\game up to the Steam install after a game patch.
# Steam writes patched files as new files, which leaves the copies' links pointing at the old ones.
# Each game file that differs (size or time) is replaced by a fresh hardlink; nothing is written in place.
# Files the patch dropped are moved to <copy>\_removed-by-patch, not deleted. BepInEx, saves and mod files are left alone.
param(
    [string[]]$Instances,
    [string]$Steam = 'C:\Program Files (x86)\Steam\steamapps\common\Reka'
)
$ErrorActionPreference = 'Stop'
if (Get-Process Reka -ErrorAction SilentlyContinue) { throw 'Close every Reka first' }
$gameRoot = Join-Path $PSScriptRoot '..\game' | Resolve-Path
if (-not $Instances) { $Instances = Get-ChildItem -LiteralPath $gameRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'Reka.exe') } | Select-Object -ExpandProperty Name }

# The Steam folder may have Reka Co-op installed (a release playtest): those files never go into the dev copies.
$modFiles = '^(BepInEx|dotnet|_RekaCoop_removed)\\|^(winhttp\.dll|doorstop_config\.ini|\.doorstop_version|changelog\.txt|README-RekaCoop\.txt|[^\\]+\.bat)$'
$steamFiles = Get-ChildItem -LiteralPath $Steam -Recurse -File | Where-Object { $_.FullName.Substring($Steam.Length).TrimStart('\') -notmatch $modFiles }
foreach ($inst in $Instances) {
    $root = Join-Path $gameRoot $inst
    $linked = 0
    foreach ($f in $steamFiles) {
        $rel = $f.FullName.Substring($Steam.Length).TrimStart('\')
        $dest = Join-Path $root $rel
        $d = Get-Item -LiteralPath $dest -ErrorAction SilentlyContinue
        if ($d -and $d.Length -eq $f.Length -and $d.LastWriteTimeUtc -eq $f.LastWriteTimeUtc) { continue }
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        if ($d) { Remove-Item -LiteralPath $dest -Force }
        New-Item -ItemType HardLink -Path $dest -Target $f.FullName | Out-Null
        $linked++
    }
    # Game files the patch removed (only under the game's own folders).
    $moved = 0
    foreach ($sub in 'Reka_Data', 'D3D12') {
        $dir = Join-Path $root $sub
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        foreach ($c in Get-ChildItem -LiteralPath $dir -Recurse -File) {
            $rel = $c.FullName.Substring($root.Length).TrimStart('\')
            if (Test-Path -LiteralPath (Join-Path $Steam $rel)) { continue }
            $to = Join-Path $root "_removed-by-patch\$rel"
            New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
            Move-Item -LiteralPath $c.FullName -Destination $to
            $moved++
        }
    }
    Write-Host "$inst`: relinked $linked, moved aside $moved"
}
