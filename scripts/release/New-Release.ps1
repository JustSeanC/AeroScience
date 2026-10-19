#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$AcceptanceRecord,
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
    if ($AcceptanceRecord) {
        $result = python scripts/release/installer_release.py finalize --folder $CandidateFolder --acceptance $AcceptanceRecord --output $OutputDirectory
    } else {
        $result = python scripts/release/automatic_release.py finalize --folder $CandidateFolder --evidence (Join-Path $OutputDirectory 'build-evidence/technical-validation.json') --output $OutputDirectory
    }
    if ($LASTEXITCODE -ne 0) { throw 'Release finalization stopped. Read the validation message above. Existing candidates and final artifacts were preserved. For v0.4.0, record genuine installed testing using Complete-ReleaseAcceptance.ps1 and supply -AcceptanceRecord.' }
    $info = $result | ConvertFrom-Json
    Write-Host "Primary installer: $OutputDirectory\$($info.asset_name)"
    Write-Host "Checksum: $OutputDirectory\$($info.asset_name).sha256"
    Write-Host "Release evidence: $OutputDirectory\$($info.setup_record_name)"
    Write-Host "Release notes: $OutputDirectory\RELEASE-NOTES.md"
    Write-Host "Source commit: $($info.source_commit)"
    Write-Host "Installer SHA-256: $($info.sha256)"
    Write-Host "Tag: $($info.tag); mark Prerelease: $($info.prerelease)"
    Write-Host 'The installer bytes are unchanged from the compiler candidate. Hardware acceptance: Not verified unless separately performed. Upload the EXE, checksum and release evidence through GitHub Releases. The portable ZIP is optional. No runner or upload was required.'
} finally { Pop-Location }
