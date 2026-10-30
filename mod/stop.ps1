$ErrorActionPreference = 'Stop'

$workspace = Join-Path $PSScriptRoot '..' | Resolve-Path
$gameRoot = (Join-Path $workspace 'game').ToString()
$prefsBackup = Join-Path $workspace 'local\playerprefs-before-dev.reg'

# Only stop test copies, never a Reka started from the Steam install.
Get-Process Reka -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($gameRoot, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host "Stopping $($_.Path) ($($_.Id))"; Stop-Process -Id $_.Id }

Start-Sleep -Seconds 2
if (Test-Path -LiteralPath $prefsBackup) {
    # reg.exe reports success on stderr, which Windows PowerShell turns into an error under 'Stop'.
    cmd /c "reg import `"$prefsBackup`" 2>nul"
    Write-Host "PlayerPrefs restored from $prefsBackup"
}
