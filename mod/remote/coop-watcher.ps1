# Runs on the second test PC, in the signed-in user's own session, from the Reka game folder.
# Lets the dev PC start and stop Reka over the folder share by dropping request files here:
#   coop-launch.request   one line of launch arguments, e.g. -coopguest   -> starts Reka through Steam
#   coop-stop.request     (any content)                                   -> closes Reka (so a new build can be copied in)
# Writes coop-watcher.status every few seconds so the dev PC can see it is alive.
# Copies this PC's own Reka saves to CoopBackups\<time> when it starts and before every launch (newest 10 kept).
# The mod never writes there (a guest plays in CoopSaves\guest), but a copy costs nothing.
# Start:  powershell -ExecutionPolicy Bypass -File .\coop-watcher.ps1     Stop: close the window.

$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
$launch = Join-Path $here 'coop-launch.request'
$stop = Join-Path $here 'coop-stop.request'
$status = Join-Path $here 'coop-watcher.status'
$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamExe
if (-not $steam) { Write-Host 'Steam not found in the registry; is it installed?'; exit 1 }
$appId = 1737870
$saves = Join-Path $env:APPDATA 'RekaGame\Saves'
$backups = Join-Path $here 'CoopBackups'

function Backup {
    if (-not (Test-Path -LiteralPath $saves)) { Write-Host "No saves at $saves to back up"; return }
    $dest = Join-Path $backups (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss')
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -LiteralPath $saves -Destination $dest -Recurse -Force
    Get-ChildItem -LiteralPath $backups -Directory | Sort-Object Name -Descending | Select-Object -Skip 10 |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    Write-Host "$(Get-Date -Format T) backed up saves to $dest"
}

# Only plain switches and values reach the game (e.g. -coopguest, -coopconnect 192.168.1.23:27877).
function Clean([string]$text) {
    $tokens = $text -split '\s+' | Where-Object { $_ -match '^[-+]?[A-Za-z0-9_.:]+$' }
    return ,$tokens
}

Write-Host "Watching $here (Steam: $steam). Leave this window open."
Backup
$nextStatus = Get-Date
while ($true) {
    if (Test-Path -LiteralPath $stop) {
        Remove-Item -LiteralPath $stop -Force
        $p = Get-Process Reka -ErrorAction SilentlyContinue
        if ($p) { $p | Stop-Process -Force; Write-Host "$(Get-Date -Format T) stopped Reka" }
    }
    if (Test-Path -LiteralPath $launch) {
        $line = (Get-Content -LiteralPath $launch -Raw)
        Remove-Item -LiteralPath $launch -Force
        if (Get-Process Reka -ErrorAction SilentlyContinue) {
            Write-Host "$(Get-Date -Format T) launch ignored: Reka is already running"
        } else {
            $gameArgs = Clean $line
            Backup
            Start-Process -FilePath $steam -ArgumentList (@('-applaunch', $appId) + $gameArgs)
            Write-Host "$(Get-Date -Format T) launched Reka $($gameArgs -join ' ')"
        }
    }
    if ((Get-Date) -ge $nextStatus) {
        $running = [bool](Get-Process Reka -ErrorAction SilentlyContinue)
        Set-Content -LiteralPath $status -Value "$(Get-Date -Format o) reka_running=$running" -Encoding ASCII
        $nextStatus = (Get-Date).AddSeconds(5)
    }
    Start-Sleep -Milliseconds 500
}
