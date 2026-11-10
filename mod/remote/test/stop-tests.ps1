# Stops the Reka tests on this PC now and keeps new ones from starting until allow-tests.ps1 is run.
# Only the test copies are closed, never a Reka started from Steam. -Once: stop this run but don't pause.
param([switch]$Once)
$root = Join-Path $PSScriptRoot '..\..\..' | Resolve-Path
if (-not $Once) { Set-Content -LiteralPath (Join-Path $root 'PAUSED') -Value (Get-Date -Format s) }
Set-Content -LiteralPath (Join-Path $root 'run.done') -Value 'stopped'
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like '*regress.py*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
& (Join-Path $root 'mod\stop.ps1')
