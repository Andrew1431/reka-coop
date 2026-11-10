# The second test bed: runs the regression on another PC over SSH (key login as $User; test copies in C:\reka-test there).
#   olga.ps1 deploy            the current mod build, scripts and save fixtures to the test PC (build first)
#   olga.ps1 run [args]        starts regress.py there with these arguments (e.g. run home guest) and returns
#   olga.ps1 status            is a run going, and the end of its output
#   olga.ps1 wait              waits for the run to end, prints its output; exit code 0 if every check passed
#   olga.ps1 stop              stops the run and the test games there now, and keeps tests off until 'allow'
#   olga.ps1 allow             tests may run there again
#   olga.ps1 sync              after a game patch: relinks the test copies there to that PC's Steam install
#   olga.ps1 setup             one-time: the launcher task and the desktop shortcuts
#   olga.ps1 ssh <command>     any PowerShell command there
# Whoever sits at that PC has the same switch: "Reka tests - STOP" and "Reka tests - allow" on the desktop.
param([Parameter(Mandatory, Position = 0)][string]$Verb, [Parameter(ValueFromRemainingArguments)][string[]]$Rest)
$ErrorActionPreference = 'Stop'
$User = 'desti'; $Name = 'Olga'; $Root = 'C:\reka-test'
$repo = Join-Path $PSScriptRoot '..' | Resolve-Path

# The name doesn't always resolve for ssh; ask Windows for the address and keep the host key under the name.
$ip = (Resolve-DnsName $Name -Type A -ErrorAction SilentlyContinue | Where-Object IPAddress | Select-Object -First 1).IPAddress
$target = "$User@$(if ($ip) { $ip } else { $Name })"
$opts = @('-o', 'BatchMode=yes', '-o', 'HostKeyAlias=olga', '-o', 'ConnectTimeout=10')
function Remote([string]$command) { & ssh @opts $target $command; if ($LASTEXITCODE -ne 0) { throw "ssh: exit $LASTEXITCODE" } }
function Script([string]$name, [string]$more = '') { Remote "powershell -NoProfile -ExecutionPolicy Bypass -File $Root\mod\remote\test\$name $more" }
function Push([string[]]$from, [string]$to) { & scp @opts -r -q @from "${target}:$($to -replace '\\', '/')"; if ($LASTEXITCODE -ne 0) { throw "scp to $to failed" } }

switch ($Verb) {
    'deploy' {
        Remote "New-Item -ItemType Directory -Force $Root\mod\remote, $Root\local\saves | Out-Null"
        Push (Get-ChildItem (Join-Path $repo 'mod\*.ps1'), (Join-Path $repo 'mod\regress.py') | ForEach-Object FullName) "$Root\mod\"
        Push (Join-Path $repo 'mod\remote\test') "$Root\mod\remote\"
        Push (Get-ChildItem (Join-Path $repo 'local\saves') -Directory | ForEach-Object FullName) "$Root\local\saves\"
        $bin = Join-Path $repo 'mod\RekaCoop\bin\Release\net6.0'
        foreach ($inst in 'play', 'client') {
            Push ("$bin\RekaCoop.dll", "$bin\RekaCoop.pdb", "$bin\LiteNetLib.dll") "$Root\game\$inst\BepInEx\plugins\RekaCoop\"
        }
        Write-Host "deployed to $target"
    }
    'run' {
        Remote "Set-Content $Root\run.args '$($Rest -join ' ')'; Remove-Item $Root\run.done -ErrorAction SilentlyContinue; Set-Content $Root\run.out ''; schtasks /run /tn RekaTestRun | Out-Null"
        Write-Host "started on $target`: regress.py $($Rest -join ' ')"
    }
    'status' { Remote "if (Test-Path $Root\PAUSED) { 'PAUSED since ' + (Get-Content $Root\PAUSED) }; if (Test-Path $Root\run.done) { 'last run ended: ' + (Get-Content $Root\run.done) } else { 'a run is going' }; Get-Content $Root\run.out -Tail 12" }
    'wait' {
        while ($true) {
            $r = & ssh @opts $target "if (Test-Path $Root\run.done) { Get-Content $Root\run.done } else { 'running' }"
            if ($r -and $r -ne 'running') { break }
            Start-Sleep -Seconds 15
        }
        Remote "Get-Content $Root\run.out"
        Write-Host "run ended: $r"
        if ($r -ne '0') { exit 1 }
    }
    'stop' { Script 'stop-tests.ps1'; Write-Host "stopped and paused on $target" }
    'allow' { Script 'allow-tests.ps1'; Write-Host "tests allowed on $target" }
    'sync' { Remote "powershell -NoProfile -ExecutionPolicy Bypass -Command `"& '$Root\mod\sync-game.ps1' -Instances play,client`"" }
    'setup' { Script 'setup.ps1' }
    'ssh' { Remote ($Rest -join ' ') }
    default { throw "unknown verb $Verb" }
}
