#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [string]$RuntimePath,
    [Parameter(Mandatory)][string]$RuntimeApprovalRecord,
    [switch]$RuntimeRedistributionConfirmed,
    [string]$OutputDirectory,
    [string]$InnoCompiler,
    [string]$VcRuntimePath,
    [switch]$PrepareOnly
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
    . "$PSScriptRoot/InnoSetup.ps1"
    $InnoCompiler = Resolve-InnoSetupCompiler -InnoCompiler $InnoCompiler
    Write-Host "Inno compiler: $InnoCompiler"
    $compilerVersion = Get-InnoSetupCompilerVersion -CompilerPath $InnoCompiler
    Write-Host "Inno compiler engine: $compilerVersion"
    $metadataJson = python scripts/release/package_release.py metadata
    if ($LASTEXITCODE -ne 0) { throw 'Release metadata is invalid.' }
    $metadata = $metadataJson | ConvertFrom-Json
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not $PrepareOnly) {
        $stateJson = python scripts/release/automatic_release.py status --output $OutputDirectory --source-commit $revision
        if ($LASTEXITCODE -ne 0) { throw 'Existing release output could not be verified.' }
        $state = $stateJson | ConvertFrom-Json
        if ($state.state -eq 'finalized') {
            Write-Host "Already finalized and verified: $OutputDirectory\$($state.info.asset_name)"
            Write-Host "Source: $revision; SHA-256: $($state.info.sha256)"
            if ($state.info.acceptance) {
                Write-Host 'Recorded installed acceptance verified for this exact installer. Nothing was rebuilt or published by this command.'
            } else {
                Write-Host 'Hardware acceptance: Not verified. GitHub publication: Not published by this command.'
            }
            return
        }
        if ($state.state -ne 'empty') { throw "$($state.state): $($state.message)" }
    }
    # Keep build outputs outside tracked source; preserve every earlier candidate/final ZIP.
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $evidence = Join-Path $OutputDirectory 'build-evidence'
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
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
    python scripts/release/verify_scientific_acceptance.py
    if ($LASTEXITCODE -ne 0) { throw 'Genuine automatic-science acceptance is missing or stale. No effortless remote-sensing claim is approved.' }
    & "$repo/scripts/build/Build-Companion.ps1"
    Copy-Item -LiteralPath 'tests/AeroScience.Tests/TestResults/windows.trx' -Destination (Join-Path $evidence 'dotnet.trx')
    python scripts/build/run_helper_tests.py --report (Join-Path $evidence 'helpers.json')
    if ($LASTEXITCODE -ne 0) { throw 'Release/helper tests failed.' }
    if (-not $env:CHROMIUM_PATH) {
        foreach ($browser in @("${env:ProgramFiles}\Microsoft\Edge\Application\msedge.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "${env:ProgramFiles}\Google\Chrome\Application\chrome.exe")) {
            if (Test-Path -LiteralPath $browser) { $env:CHROMIUM_PATH = $browser; break }
        }
    }
    if (-not $env:CHROMIUM_PATH) { npx playwright install chromium; if ($LASTEXITCODE -ne 0) { throw 'Browser test tooling unavailable. Install Chromium/Edge/Chrome for the development build; no tests were marked passed.' } }
    npm run test:web
    if ($LASTEXITCODE -ne 0) { throw 'Browser tests failed. No final installer was produced.' }
    Copy-Item -LiteralPath 'artifacts/web-results.json' -Destination (Join-Path $evidence 'browser.json')
    # Native Tools shells can set Platform=x64; use an explicit output for this test.
    $recoveryHostDirectory = Join-Path $repo 'artifacts/recovery-test-host'
    dotnet build tests/AeroScience.TestHost -c Release --no-restore --nologo --output $recoveryHostDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Recovery test host build failed.' }
    python scripts/build/smoke_recovery.py --host-dll (Join-Path $recoveryHostDirectory 'AeroScience.TestHost.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Recording recovery smoke failed.' }
    @{ result='passed'; check='Actual-process crash/restart, committed samples/modes and exclusive owner' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'recovery.json') -Encoding utf8
    python scripts/release/automatic_release.py evidence --unit (Join-Path $evidence 'dotnet.trx') --browser (Join-Path $evidence 'browser.json') --helpers (Join-Path $evidence 'helpers.json') --recovery (Join-Path $evidence 'recovery.json') --source-commit $revision --output (Join-Path $evidence 'technical-validation.json')
    if ($LASTEXITCODE -ne 0) { throw 'Automatic test evidence validation failed.' }
    & "$repo/scripts/build/Build-Toolbar.ps1" -SdkRoot $SdkRoot
    $runtimeReport = Join-Path $OutputDirectory ("runtime-check-" + [Guid]::NewGuid().ToString('N') + '.json')
    & "$repo/scripts/build/Test-SimConnectRuntime.ps1" -RuntimePath $runtime -ReportPath $runtimeReport
    Copy-Item -LiteralPath $runtime -Destination "$repo/artifacts/Windows-Companion/SimConnect.dll"
    $package = @(Get-ChildItem "$repo/artifacts/toolbar-full/output" -Filter manifest.json -Recurse | Where-Object { $_.Directory.Name -eq 'aeroscience-toolbar' })
    if ($package.Count -ne 1) { throw 'Expected exactly one genuine full-toolbar SDK output.' }
    python scripts/release/package_release.py prepare --companion artifacts/Windows-Companion --community $package[0].Directory.FullName --sdk-receipt artifacts/toolbar-full/sdk-build.json --runtime-report $runtimeReport --runtime-approval $approval --destination $candidate --output $candidateZip --runtime-redistribution-confirmed
    if ($LASTEXITCODE -ne 0) { throw 'Candidate validation failed. No final release was produced.' }
    & "$repo/scripts/release/Build-Installer.ps1" -CandidateFolder $candidate -OutputDirectory $OutputDirectory -InnoCompiler $InnoCompiler -VcRuntimePath $VcRuntimePath
    Write-Host "Source: $revision"
    Write-Host "Candidate ZIP: $candidateZip"
    Write-Host "Candidate files: $candidate"
    Write-Host "Primary installer candidate: $OutputDirectory\installer-candidate\$($metadata.setup_name)"
    # Keep scientific releases at the successfully built candidate until actual
    # installed acceptance is recorded. The check belongs on finalization, not
    # on the earlier output-status inspection.
    if (-not $PrepareOnly -and [version]($metadata.package_version) -lt [version]'0.4.0') {
        & "$repo/scripts/release/New-Release.ps1" -OutputDirectory $OutputDirectory
        Write-Host "Version: $($metadata.version); Source: $revision"
        Write-Host 'Build: Passed. Automated .NET/browser/helper/recovery checks: Passed. Official SDK package/ABI/runtime checks: Passed. Installer compilation/integrity: Passed.'
        Write-Host 'Hardware acceptance: Not verified. GitHub publication: Not published. Upload the final setup EXE, checksum and public release evidence manually through GitHub Releases.'
    } else {
        Write-Host 'Candidate ready for actual Windows/MSFS testing. Hardware acceptance is NOT verified; nothing was published.'
        Write-Host "After the checks in docs/v0.4.0-windows-testing.md, run pwsh -STA -File scripts/release/Complete-ReleaseAcceptance.ps1 -AcceptanceRecord '$OutputDirectory\installer-candidate\ACCEPTANCE.json'. The graphical checklist records your actual results without hand-editing JSON."
        Write-Host 'Finalize the same tested installer with New-Release.ps1 -AcceptanceRecord; do not rebuild between testing and finalization.'
    }
} finally { Pop-Location }
