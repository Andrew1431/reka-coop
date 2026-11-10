# One-time setup on the test PC (run over SSH from olga.ps1 setup): the task that starts a run in the desktop session,
# and two desktop shortcuts for whoever sits at the PC: stop the tests now, and allow them again.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$ps = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File'

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "$ps `"$here\run.ps1`""
$principal = New-ScheduledTaskPrincipal -UserId (whoami) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'RekaTestRun' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Write-Host 'task RekaTestRun registered'

$desktop = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
foreach ($s in @(@('Reka tests - STOP', 'stop-tests.ps1', 'Stops the Reka co-op tests on this PC and keeps them off'),
                 @('Reka tests - allow', 'allow-tests.ps1', 'Lets the Reka co-op tests run on this PC again'))) {
    $lnk = $shell.CreateShortcut((Join-Path $desktop "$($s[0]).lnk"))
    $lnk.TargetPath = 'powershell.exe'
    $lnk.Arguments = "$ps `"$here\$($s[1])`""
    $lnk.Description = $s[2]
    $lnk.IconLocation = if ($s[1] -like 'stop*') { 'shell32.dll,131' } else { 'shell32.dll,137' }
    $lnk.Save()
    Write-Host "shortcut $($s[0]) on $desktop"
}
