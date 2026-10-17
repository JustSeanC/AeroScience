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
    $json = python scripts/release/package_release.py metadata
    if ($LASTEXITCODE -ne 0) { throw 'Release metadata is invalid.' }
    $metadata = $json | ConvertFrom-Json
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not $CandidateFolder) { $CandidateFolder = Join-Path $OutputDirectory ("release-candidate/" + $metadata.root_name) }
    $output = Join-Path $OutputDirectory $metadata.asset_name
    python scripts/release/package_release.py release --candidate $CandidateFolder --acceptance $AcceptanceRecord --output $output
    if ($LASTEXITCODE -ne 0) { throw 'Release packaging failed. Resolve the stated validation/acceptance issue; do not upload the candidate.' }
    $notes = Join-Path $OutputDirectory 'RELEASE-NOTES.md'
    $verifiedJson = python scripts/release/package_release.py verify-release --archive $output --checksum "$output.sha256" --notes-output $notes
    if ($LASTEXITCODE -ne 0) { throw 'Final ZIP verification failed.' }
    $verified = $verifiedJson | ConvertFrom-Json
    Write-Host "Ready for manual GitHub web upload: $output"
    Write-Host "Checksum: $output.sha256"
    Write-Host "Release notes: $notes"
    Write-Host "Tested source commit: $($verified.source_commit)"
    Write-Host "Final SHA-256: $($verified.sha256)"
    Write-Host "Tag: $($metadata.tag); mark Prerelease: $($metadata.prerelease)"
    Write-Host 'No upload or GitHub Actions runner is required by these build scripts. Follow docs/public-release-windows.md.'
} finally { Pop-Location }
