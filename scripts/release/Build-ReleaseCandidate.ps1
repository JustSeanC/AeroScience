#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [string]$RuntimePath,
    [Parameter(Mandatory)][string]$RuntimeApprovalRecord,
    [switch]$RuntimeRedistributionConfirmed,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Build the complete release candidate on Windows x64 with the official installed SDK. Linux cross-publishing cannot replace this step.'
}
if (-not $RuntimeRedistributionConfirmed) { throw 'Document permission for this native SimConnect.dll before preparing a distributable candidate. Read docs/runtime-distribution.md.' }
foreach ($tool in @('git', 'python', 'dotnet', 'npm', 'cl.exe')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Missing maintainer tool: $tool. Use an x64 Native Tools shell with PowerShell 7." }
}
Push-Location $repo
try {
    $revision = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-fA-F0-9]{40}$') { throw 'Use a Git checkout of the tested source, not GitHub source ZIP files.' }
    if (git status --porcelain) { throw 'Commit or preserve local source changes before building; releases require a clean checkout.' }
    $metadataJson = python scripts/release/package_release.py metadata
    if ($LASTEXITCODE -ne 0) { throw 'Release metadata is invalid.' }
    $metadata = $metadataJson | ConvertFrom-Json
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    # Keep build outputs outside tracked source; preserve every earlier candidate/final ZIP.
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $candidate = Join-Path $OutputDirectory ("release-candidate/" + $metadata.root_name)
    $candidateZip = Join-Path $OutputDirectory $metadata.candidate_name
    if ((Test-Path $candidate) -or (Test-Path $candidateZip)) { throw 'An earlier candidate already exists. Keep it, and choose another -OutputDirectory for a new build.' }
    if (-not $RuntimePath) { $RuntimePath = Join-Path $SdkRoot 'SimConnect SDK/lib/SimConnect.dll' }
    $approval = (Resolve-Path -LiteralPath $RuntimeApprovalRecord).Path
    $runtime = (Resolve-Path -LiteralPath $RuntimePath).Path
    $review = Get-Content -LiteralPath $approval -Raw | ConvertFrom-Json
    if ($review.format_version -ne 2 -or $review.redistribution_permitted -ne $true -or $review.conditions_reviewed -ne $true -or
        -not $review.conditions_summary -or -not $review.permission_summary -or $review.supporting_evidence_private -ne $true -or
        $review.runtime_sha256 -ne (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Generate the private-safe version 2 runtime attestation with New-RuntimeApproval.ps1. Record applicable conditions and match the actual DLL; original correspondence remains private.'
    }
    & "$repo/scripts/build/Build-Companion.ps1"
    python -m unittest discover -s tests -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Release/helper tests failed.' }
    # Native Tools shells can set Platform=x64; use an explicit output for this test.
    $recoveryHostDirectory = Join-Path $repo 'artifacts/recovery-test-host'
    dotnet build tests/AeroScience.TestHost -c Release --no-restore --nologo --output $recoveryHostDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Recovery test host build failed.' }
    python scripts/build/smoke_recovery.py --host-dll (Join-Path $recoveryHostDirectory 'AeroScience.TestHost.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Recording recovery smoke failed.' }
    & "$repo/scripts/build/Build-Toolbar.ps1" -SdkRoot $SdkRoot
    $runtimeReport = Join-Path $OutputDirectory ("runtime-check-" + [Guid]::NewGuid().ToString('N') + '.json')
    & "$repo/scripts/build/Test-SimConnectRuntime.ps1" -RuntimePath $runtime -ReportPath $runtimeReport
    Copy-Item -LiteralPath $runtime -Destination "$repo/artifacts/Windows-Companion/SimConnect.dll"
    $package = @(Get-ChildItem "$repo/artifacts/toolbar-full/output" -Filter manifest.json -Recurse | Where-Object { $_.Directory.Name -eq 'aeroscience-toolbar' })
    if ($package.Count -ne 1) { throw 'Expected exactly one genuine full-toolbar SDK output.' }
    python scripts/release/package_release.py prepare --companion artifacts/Windows-Companion --community $package[0].Directory.FullName --sdk-receipt artifacts/toolbar-full/sdk-build.json --runtime-report $runtimeReport --runtime-approval $approval --destination $candidate --output $candidateZip --runtime-redistribution-confirmed
    if ($LASTEXITCODE -ne 0) { throw 'Candidate validation failed. No final release was produced.' }
    Write-Host "Source: $revision"
    Write-Host "Candidate ZIP: $candidateZip"
    Write-Host "Candidate files: $candidate"
    Write-Host 'Extract that ZIP to a separate test folder. Complete the minimum real Windows/MSFS checks and save an acceptance record. Finalize with New-Release.ps1 without rebuilding. Nothing has been uploaded.'
} finally { Pop-Location }
