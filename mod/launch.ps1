[CmdletBinding()]
param(
    [string[]]$Instances = @('host', 'client'),
    # Instance names (save folder CoopSaves\<name>), in order; default: the game folder's name.
    [string[]]$Names = @(),
    [int]$Port = 27877,
    # Extra Unity/game arguments for every instance.
    [string[]]$ExtraArgs = @(),
    # Extra arguments for every instance except the host, e.g. -coopbotwalk.
    [string[]]$ClientArgs = @(),
    # Per instance, in order: 'monitor:percent' (0 = primary), e.g. '0:80','1:80'. Default: side-by-side tiles.
    [string[]]$Screens = @(),
    # Every instance joins an already running host instead of the first one hosting.
    [switch]$JoinOnly
)

$ErrorActionPreference = 'Stop'

$workspace = Join-Path $PSScriptRoot '..' | Resolve-Path
$gameRoot = Join-Path $workspace 'game'
# [char]0xE4 is the a-umlaut; spelled out because Windows PowerShell reads BOM-less scripts as ANSI.
$prefsKey = "HKCU\Software\Emberstorm Entertainment UG (haftungsbeschr$([char]0xE4)nkt)\Reka"
$prefsBackup = Join-Path $workspace 'local\playerprefs-before-dev.reg'

# Test copies share PlayerPrefs (graphics, audio, keybinds) with the real game through the registry.
# Snapshot them before a dev session so stop.ps1 can put the player's settings back.
if (-not (Get-Process Reka -ErrorAction SilentlyContinue)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $prefsBackup) | Out-Null
    # The key's real name, found without spelling the umlaut (a console in another code page can mangle it).
    $found = Get-Item -Path 'HKCU:\Software\Emberstorm*\Reka' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $prefsKey = $found.Name }
    $tmp = "$prefsBackup.new"
    & reg export $prefsKey $tmp /y 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { Move-Item -LiteralPath $tmp -Destination $prefsBackup -Force; Write-Host "PlayerPrefs snapshot -> $prefsBackup" }
    else { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue; Write-Host "PlayerPrefs snapshot skipped (key not readable); keeping the previous one" }
}

# The first instance hosts; the rest join it over loopback.
for ($i = 0; $i -lt $Instances.Count; $i++) {
    $name = $Instances[$i]
    $dir = Join-Path $gameRoot $name
    $session = if ($i -eq 0 -and -not $JoinOnly) { @('-coophost', "$Port") } else { @('-coopconnect', "127.0.0.1:$Port") + $ClientArgs }
    $instName = if ($i -lt $Names.Count) { $Names[$i] } else { $name }
    $arguments = @('-coopinstance', $instName, '-logFile', (Join-Path $dir "player-$name.log"),
                   '-screen-fullscreen', '0') + $session + $ExtraArgs
    $arguments += if ($i -lt $Screens.Count) { @('-coopscreen', $Screens[$i]) } else { @('-cooptile', "$i/$($Instances.Count)") }
    $p = Start-Process -FilePath (Join-Path $dir 'Reka.exe') -WorkingDirectory $dir -ArgumentList $arguments -PassThru
    Write-Host "$name pid $($p.Id)"
}
