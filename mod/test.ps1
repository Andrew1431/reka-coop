# Offline tests (no game needed): real sessions over loopback. See RekaCoop.Tests/Program.cs.
dotnet run --project (Join-Path $PSScriptRoot 'RekaCoop.Tests') -c Release
exit $LASTEXITCODE
