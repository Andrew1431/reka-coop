param([Parameter(Mandatory)][string]$Command, [string[]]$Instances = @('host'))

# Sends a dev command to running test copies (see RekaCoop/Dev/Commands.cs). Output appears in each copy's LogOutput.log.
$gameRoot = Join-Path $PSScriptRoot '..\game' | Resolve-Path
foreach ($name in $Instances) {
    Set-Content -LiteralPath (Join-Path $gameRoot "$name\cmd.request") -Value $Command -Encoding ASCII
}
