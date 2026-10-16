#Requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    dotnet restore AeroScience.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet test AeroScience.sln -c $Configuration --no-restore --logger 'trx;LogFileName=windows.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Browser tooling restore failed.' }
    npm run check:web
    if ($LASTEXITCODE -ne 0) { throw 'Web JavaScript validation failed.' }
    dotnet publish src/AeroScience.Engine -c $Configuration -r win-x64 --self-contained true --no-restore -o artifacts/Windows-Companion
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item scripts/release/Launch-*.cmd artifacts/Windows-Companion/
    Write-Host 'Self-contained Windows companion built. Simulated mode is standalone. Real SimConnect/toolbar use additionally needs the official x64 runtime; see docs/simconnect.md.'
} finally { Pop-Location }
