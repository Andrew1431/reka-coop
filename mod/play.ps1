[CmdletBinding()]
param(
    # Start only the host (the guest joins later with -Join).
    [switch]$HostOnly,
    # Start only the guest, joining the running host.
    [switch]$Join,
    [int]$Percent = 80
)

# Play session: the 'play' copy hosts on the primary monitor, the 'client' copy joins on the second monitor,
# both windowed at $Percent of their screen. The host starts at the main menu (new game or load, your pick).
$launch = Join-Path $PSScriptRoot 'launch.ps1'
if (-not $Join) { & $launch -Instances play -Screens "0:$Percent" }
if (-not $HostOnly) {
    if (-not $Join) { Start-Sleep -Seconds 2 }
    & $launch -Instances client -Screens "1:$Percent" -JoinOnly
}
