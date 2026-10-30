param([string[]]$Instances = @('host', 'client'), [int]$TimeoutSeconds = 10)

# Asks each running test copy for an in-game screenshot (see Dev/Shot.cs) and waits for the files.
$gameRoot = Join-Path $PSScriptRoot '..\game' | Resolve-Path
$started = Get-Date
foreach ($name in $Instances) {
    $dir = Join-Path $gameRoot $name
    Remove-Item -LiteralPath (Join-Path $dir 'shot.png') -ErrorAction SilentlyContinue
    New-Item -ItemType File -Force -Path (Join-Path $dir 'shot.request') | Out-Null
}
foreach ($name in $Instances) {
    $png = Join-Path $gameRoot "$name\shot.png"
    while (-not (Test-Path -LiteralPath $png) -and ((Get-Date) - $started).TotalSeconds -lt $TimeoutSeconds) { Start-Sleep -Milliseconds 250 }
    if (Test-Path -LiteralPath $png) { Start-Sleep -Milliseconds 500; Write-Host $png } else { Write-Warning "$name gave no screenshot" }
}
