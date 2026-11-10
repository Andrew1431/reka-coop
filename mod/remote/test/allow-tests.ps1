# Lets the Reka tests run on this PC again (undoes stop-tests.ps1). Starts nothing by itself.
$root = Join-Path $PSScriptRoot '..\..\..' | Resolve-Path
Remove-Item -LiteralPath (Join-Path $root 'PAUSED') -ErrorAction SilentlyContinue
