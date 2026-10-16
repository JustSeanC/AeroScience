#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$DataDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AeroScience'),
    [string]$OutputDirectory = ([Environment]::GetFolderPath('Desktop')),
    [string]$BuildLogDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { throw 'Specify -OutputDirectory for the feedback ZIP.' }
$tag = 'AeroScience-Feedback-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stage = Join-Path ([IO.Path]::GetTempPath()) $tag
$output = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ($tag + '.zip')))
function Copy-RedactedText([string]$Source, [string]$Destination) {
    $text = [IO.File]::ReadAllText($Source)
    if ($env:USERPROFILE) { $text = $text.Replace($env:USERPROFILE, '%USERPROFILE%') }
    [IO.File]::WriteAllText($Destination, $text, (New-Object Text.UTF8Encoding($false)))
}
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $logRoot = Join-Path $DataDirectory 'logs'
    if (Test-Path -LiteralPath $logRoot) {
        $logs = @(Get-ChildItem -LiteralPath $logRoot -Filter 'engine-*.log' -File | Sort-Object Name -Descending | Select-Object -First 7)
        $logStage = Join-Path $stage 'logs'
        New-Item -ItemType Directory -Path $logStage | Out-Null
        foreach ($log in $logs) { Copy-RedactedText $log.FullName (Join-Path $logStage $log.Name) }
    }
    if ($BuildLogDirectory) {
        $buildStage = Join-Path $stage 'build'
        New-Item -ItemType Directory -Path $buildStage | Out-Null
        # Explicit text reports only. Never include SDK binaries, entire build trees, or credential files.
        foreach ($name in @('build.log', 'sdk-contract.log', 'sdk-build.json', 'runtime-check.json', 'startup-console.txt')) {
            $source = Join-Path $BuildLogDirectory $name
            if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-RedactedText $source (Join-Path $buildStage $name) }
        }
    }
    $binaries = @()
    foreach ($name in @('AeroScience.Engine.exe', 'SimConnect.dll')) {
        $binary = Join-Path $PSScriptRoot $name
        if (Test-Path -LiteralPath $binary -PathType Leaf) {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
            $binaries += @{ file=$name; version=$info.FileVersion; sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    }
    @{
        collected_utc=[DateTime]::UtcNow.ToString('o'); os=[Environment]::OSVersion.VersionString;
        is_64_bit_os=[Environment]::Is64BitOperatingSystem; binaries=$binaries;
        mission_database_included=$false; environment_dump_included=$false; pairing_code_collected=$false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'system.json') -Encoding utf8
    @'
AeroScience v0.1.0 testing feedback

UTC time of problem:
MSFS version / Store or Steam:
SDK version:
Aircraft title / add-on version:
Telemetry source (Simulated or SimConnect):
Science mode (Universal or Realistic):
Phase (desktop / toolbar POC / full toolbar / Quest 3 VR / iPad Safari):
Steps to reproduce:
Expected result:
Actual result / exact error / HRESULT:
Mission ID and sample count (if relevant):
VR runtime and connection method (if relevant):
Device, browser version, orientation (if relevant):

Add screenshots without the pairing code. Read the ZIP contents before sharing.
This bundle excludes the SQLite database, telemetry exports, SDK DLLs, environment
dump, cookies, session tokens and pairing code. If recording data is needed,
share an explicitly chosen test mission export separately.
'@ | Set-Content -LiteralPath (Join-Path $stage 'FEEDBACK.txt') -Encoding utf8
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    Compress-Archive -LiteralPath $stage -DestinationPath $output -CompressionLevel Optimal
    Write-Host "Feedback ZIP: $output"
    Write-Host 'Extract/review it, fill in FEEDBACK.txt, then share the reviewed files. No report was uploaded.'
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
