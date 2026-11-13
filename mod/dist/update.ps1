param(
    [Parameter(Mandatory)][string]$GameDir,
    [string]$Repo = 'Andrew1431/reka-coop',
    [string]$Zip  # a release zip already on disk (tests); skips GitHub
)

# Run by "Update Co-op.bat" with Reka closed. Fetches the newest Reka Co-op release from GitHub, checks its SHA-256
# against the one GitHub lists for the asset, and replaces the mod's files. Saves and BepInEx\config are never touched;
# the rest of BepInEx is only replaced when the release ships a different BepInEx build.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is many times slower with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$plugin = Join-Path $GameDir 'BepInEx\plugins\RekaCoop'
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Reka.exe'))) { throw "Reka.exe isn't in $GameDir. Run this from the game folder." }
if (Get-Process -Name Reka -ErrorAction SilentlyContinue) { throw 'Reka is running. Close it first, then run Update Co-op again.' }

function Read-Info($path) {
    $info = @{}
    if (Test-Path -LiteralPath $path) {
        foreach ($line in Get-Content -LiteralPath $path) { if ($line -match '^(\w+)=(.*)$') { $info[$Matches[1]] = $Matches[2].Trim() } }
    }
    $info
}
function Core($v) { [version]([regex]::Match($v, '^\d+(\.\d+){1,3}').Value) }

$have = Read-Info (Join-Path $plugin 'version.txt')
$haveVersion = if ($have.mod) { $have.mod } else { '0.0' }
Write-Host "Installed: Reka Co-op $haveVersion"

$work = Join-Path $env:TEMP "RekaCoopUpdate"
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

if (-not $Zip) {
    $headers = @{ 'User-Agent' = 'RekaCoop-updater'; 'Accept' = 'application/vnd.github+json' }
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=10" -Headers $headers
    $release = $releases | Where-Object { -not $_.draft } | Select-Object -First 1
    if (-not $release) { Write-Host 'No releases yet.'; return }
    $latest = $release.tag_name -replace '^v', ''
    if ((Core $latest) -le (Core $haveVersion)) { Write-Host "Up to date ($latest is the newest)."; return }
    $asset = $release.assets | Where-Object { $_.name -like 'RekaCoop-*.zip' } | Select-Object -First 1
    if (-not $asset) { throw "Release $latest has no RekaCoop zip." }
    Write-Host "Downloading Reka Co-op $latest ($([math]::Round($asset.size / 1MB, 1)) MB)..."
    $Zip = Join-Path $work $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $Zip -Headers $headers -UseBasicParsing

    # GitHub computes the digest itself on upload; older releases fall back to a .sha256 asset next to the zip.
    $want = if ($asset.digest) { $asset.digest -replace '^sha256:', '' } else {
        $sum = $release.assets | Where-Object { $_.name -eq "$($asset.name).sha256" } | Select-Object -First 1
        if (-not $sum) { throw 'The release has no checksum; not installing it.' }
        ((Invoke-WebRequest -Uri $sum.browser_download_url -Headers $headers -UseBasicParsing).Content -split '\s+')[0]
    }
    $got = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
    if ($got -ne $want.ToUpperInvariant()) { throw "Checksum mismatch (got $got, expected $want). Nothing was changed." }
    Write-Host 'Checksum OK.'
}

$new = Join-Path $work 'new'
Expand-Archive -LiteralPath $Zip -DestinationPath $new
$newPlugin = Join-Path $new 'BepInEx\plugins\RekaCoop'
if (-not (Test-Path -LiteralPath (Join-Path $newPlugin 'RekaCoop.dll'))) { throw 'The download has no RekaCoop.dll. Nothing was changed.' }
$got = Read-Info (Join-Path $newPlugin 'version.txt')

# A new BepInEx build: replace BepInEx's own files, keep config and other plugins' folders.
if ($got.bepinex -and $got.bepinex -ne $have.bepinex) {
    Write-Host "Updating BepInEx to $($got.bepinex)..."
    foreach ($dir in 'BepInEx\core', 'dotnet') {
        $src = Join-Path $new $dir
        if (Test-Path -LiteralPath $src) { Copy-Item -Path (Join-Path $src '*') -Destination (Join-Path $GameDir $dir) -Recurse -Force }
    }
    Get-ChildItem -LiteralPath $new -File | Where-Object { $_.Name -in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version' } |
        Copy-Item -Destination $GameDir -Force
    # Switched off with "Disable Co-op" (winhttp.dll.off): the new loader stays off too.
    $offLoader = Join-Path $GameDir 'winhttp.dll.off'
    if (Test-Path -LiteralPath $offLoader) { Move-Item -LiteralPath (Join-Path $GameDir 'winhttp.dll') -Destination $offLoader -Force }
}

# The mod: keep it off if it was switched off.
$off = (Test-Path -LiteralPath (Join-Path $plugin 'RekaCoop.dll.off')) -and -not (Test-Path -LiteralPath (Join-Path $plugin 'RekaCoop.dll'))
New-Item -ItemType Directory -Force -Path $plugin | Out-Null
Copy-Item -Path (Join-Path $newPlugin '*') -Destination $plugin -Recurse -Force
if ($off) { Move-Item -LiteralPath (Join-Path $plugin 'RekaCoop.dll') -Destination (Join-Path $plugin 'RekaCoop.dll.off') -Force }

Get-ChildItem -LiteralPath $new -File | Where-Object { $_.Extension -eq '.bat' -or $_.Name -eq 'README-RekaCoop.txt' } |
    Copy-Item -Destination $GameDir -Force

Remove-Item -LiteralPath $work -Recurse -Force
$off = $off -or ((Test-Path -LiteralPath (Join-Path $GameDir 'winhttp.dll.off')) -and -not (Test-Path -LiteralPath (Join-Path $GameDir 'winhttp.dll')))
Write-Host "Updated to Reka Co-op $($got.mod)$(if ($off) { ' (still switched off: run Enable Co-op to turn it on)' })."
