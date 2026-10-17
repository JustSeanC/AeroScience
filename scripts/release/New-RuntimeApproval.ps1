#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RuntimePath = 'C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll',
    [string]$OutputPath = 'C:\AeroScience-Release-Records\runtime-approval.json',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ConditionsSummary,
    [string]$ReviewedBy = 'AeroScience maintainer',
    [switch]$RedistributionAuthorized
)
$ErrorActionPreference = 'Stop'
if (-not $RedistributionAuthorized) { throw 'Record the confirmed maintainer authorization with -RedistributionAuthorized. The original correspondence is not an input.' }
if ([string]::IsNullOrWhiteSpace($ConditionsSummary) -or [string]::IsNullOrWhiteSpace($ReviewedBy)) {
    throw 'Provide a public conditions summary and public reviewer label; keep correspondence and private identities outside the record.'
}
$runtimeFile = (Resolve-Path -LiteralPath $RuntimePath).Path
if (-not [IO.Path]::GetFileName($runtimeFile).Equals('SimConnect.dll', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Select the official native MSFS 2024 SimConnect.dll. Its actual Windows loading/exports are checked by the candidate builder.'
}
$recordPath = [IO.Path]::GetFullPath($OutputPath)
$repo = (Resolve-Path "$PSScriptRoot/../..").Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($recordPath.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep release approval records outside the source checkout, for example C:\AeroScience-Release-Records.'
}
$record = [ordered]@{
    format_version = 2
    redistribution_permitted = $true
    permission_for_file = 'SimConnect.dll'
    runtime_sha256 = (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash.ToLowerInvariant()
    permission_source = 'Project maintainer confirmation; supporting authorization retained privately.'
    permission_summary = 'The project maintainer confirms authorization to redistribute the native MSFS 2024 SimConnect.dll with AeroScience.'
    conditions_reviewed = $true
    conditions_summary = $ConditionsSummary.Trim()
    supporting_evidence_private = $true
    reviewed_by = $ReviewedBy.Trim()
    reviewed_utc = [DateTime]::UtcNow.ToString('o')
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($recordPath)) | Out-Null
# CreateNew preserves any earlier approval record; there is no correspondence-copy step.
$stream = [IO.File]::Open($recordPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 4) + [Environment]::NewLine)
    $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true)
} finally { $stream.Dispose() }
Write-Host "Public runtime attestation saved: $recordPath"
Write-Host "Runtime SHA-256: $($record.runtime_sha256)"
Write-Host 'Authorization and conditions are recorded. Build-ReleaseCandidate.ps1 separately verifies the official SDK build, native DLL loading/exports and distributable.'
