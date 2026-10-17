#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AcceptanceRecord,
    [string]$CandidateFolder,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not $CandidateFolder) { $CandidateFolder = Join-Path $OutputDirectory 'installer-candidate' }
    $result = python scripts/release/installer_release.py finalize --folder $CandidateFolder --acceptance $AcceptanceRecord --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Installer release validation failed. Complete genuine acceptance for this exact setup EXE; do not publish the candidate.' }
    $info = $result | ConvertFrom-Json
    Write-Host "Primary installer: $OutputDirectory\$($info.asset_name)"
    Write-Host "Checksum: $OutputDirectory\$($info.asset_name).sha256"
    Write-Host "Release evidence: $OutputDirectory\$($info.setup_record_name)"
    Write-Host "Release notes: $OutputDirectory\RELEASE-NOTES.md"
    Write-Host "Tested source commit: $($info.source_commit)"
    Write-Host "Installer SHA-256: $($info.sha256)"
    Write-Host "Tag: $($info.tag); mark Prerelease: $($info.prerelease)"
    Write-Host 'The installer bytes are unchanged from the tested candidate. Upload the EXE, its checksum and release evidence through GitHub Releases. The portable ZIP is optional. No runner or upload was required.'
} finally { Pop-Location }
