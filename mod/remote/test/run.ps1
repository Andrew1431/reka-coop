# Runs on the test PC, started by the scheduled task RekaTestRun (in the logged-in desktop session: the games need one).
# Runs regress.py with the arguments in ..\..\..\run.args; output goes to run.out, the exit code to run.done.
$root = Join-Path $PSScriptRoot '..\..\..' | Resolve-Path
$out = Join-Path $root 'run.out'
$done = Join-Path $root 'run.done'
Remove-Item -LiteralPath $done -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath (Join-Path $root 'PAUSED')) {
    Set-Content -LiteralPath $out -Value 'Tests are paused on this PC ("Reka tests - allow" on the desktop turns them back on).'
    Set-Content -LiteralPath $done -Value 'paused'
    exit
}
$argLine = (Get-Content -LiteralPath (Join-Path $root 'run.args') -ErrorAction SilentlyContinue | Select-Object -First 1)
$py = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'
cmd /c "`"$py`" -u `"$(Join-Path $root 'mod\regress.py')`" $argLine > `"$out`" 2>&1"
if (-not (Test-Path -LiteralPath $done)) { Set-Content -LiteralPath $done -Value $LASTEXITCODE }
