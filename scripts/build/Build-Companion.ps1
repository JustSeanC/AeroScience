#Requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
foreach ($command in @('dotnet', 'npm')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required development tool is missing from PATH: $command" }
}
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
    dotnet publish src/AeroScience.Desktop -c $Configuration -r win-x64 --self-contained true --no-restore -o artifacts/Windows-Companion
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    foreach ($legacy in @('AeroScience.Engine.exe', 'createdump.exe', 'Launch-Simulated.cmd', 'Launch-SimConnect.cmd', 'Collect-Feedback.ps1', 'Test-SimConnectRuntime.ps1', 'SimConnect.dll')) {
        $path = Join-Path artifacts/Windows-Companion $legacy
        if (Test-Path $path) { Remove-Item $path }
    }
    Get-ChildItem artifacts/Windows-Companion -Filter *.pdb -Recurse -File | Remove-Item
    $hasGit = (Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $repo '.git'))
    $revision = if ($hasGit) { (git rev-parse HEAD).Trim() } else { 'unknown' }
    $dirty = if ($hasGit) { [bool](git status --porcelain) } else { $true }
    $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    @{ version=$version; source_commit=$revision; source_dirty=$dirty; target='win-x64'; built_utc=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content artifacts/Windows-Companion/build-info.json -Encoding utf8
    Write-Host 'Windows app built: artifacts/Windows-Companion/AeroScience.exe. No tester commands are required. A complete alpha still requires genuine SDK toolbar output, approved runtime distribution and hardware acceptance.'
} finally { Pop-Location }
