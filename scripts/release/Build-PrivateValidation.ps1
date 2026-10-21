#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [Parameter(Mandatory)][string]$RuntimeApprovalRecord,
    [switch]$RuntimeRedistributionConfirmed,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'Use Windows x64 and the genuine installed MSFS 2024 SDK.' }
if (-not $RuntimeRedistributionConfirmed) { throw 'The existing hash-bound runtime authorization is required. Keep its correspondence private.' }
Push-Location $repo
try {
    $branch = (git branch --show-current).Trim()
    $revision = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'develop/v0.5.0' -or $revision -notmatch '^[a-f0-9]{40}$' -or (git status --porcelain)) { throw 'Use a clean committed develop/v0.5.0 checkout. Nothing was merged or published.' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Preserve earlier candidates. Choose a new, nonexistent output directory.' }
    $protectedRoots = @((Join-Path $env:LOCALAPPDATA 'AeroScience'), (Join-Path $env:LOCALAPPDATA 'Programs/AeroScience'), (Join-Path $env:LOCALAPPDATA 'AeroScience-Validation-v0.5.0'))
    $production = Get-ItemProperty -LiteralPath 'HKCU:\Software\AeroScience' -ErrorAction SilentlyContinue
    foreach ($name in @('InstallDirectory','MissionDirectory','ScienceLibraryDirectory')) {
        if ($production -and $production.$name) { $protectedRoots += [string]$production.$name }
    }
    foreach ($protected in $protectedRoots) {
        $boundary = [IO.Path]::GetFullPath($protected).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $candidateBoundary = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($candidateBoundary.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or $boundary.StartsWith($candidateBoundary,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Build outside production installation/data and the validation profile. No existing location was modified.'
        }
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $app = Join-Path $output 'AeroScience-0.5.0-Validation'
    # Public gate results are evidence, never permission to publish this profile.
    $PSNativeCommandUseErrorActionPreference = $false
    python scripts/release/verify_scientific_acceptance.py *> (Join-Path $output 'public-scientific-gate.txt')
    $gatePassed = $LASTEXITCODE -eq 0
    & "$repo/scripts/build/Build-Companion.ps1"
    # Separate intermediates prevent profile metadata leaking into production.
    & "$repo/scripts/build/Publish-ValidationCompanion.ps1" -ArtifactsPath (Join-Path $output 'intermediate') -OutputDirectory $app
    Get-ChildItem $app -Filter *.pdb -File -Recurse | Remove-Item
    & "$repo/scripts/build/Build-Toolbar.ps1" -SdkRoot $SdkRoot -ValidationProfile
    $packages = @(Get-ChildItem "$repo/artifacts/toolbar-validation/output" -Filter manifest.json -Recurse | Where-Object { $_.Directory.Name -eq 'aeroscience-validation-toolbar' })
    if ($packages.Count -ne 1) { throw 'Expected one genuine validation SDK package.' }
    $toolbar = Join-Path $app 'Toolbar'
    [IO.Directory]::CreateDirectory($toolbar) | Out-Null
    Copy-Item -LiteralPath $packages[0].Directory.FullName -Destination $toolbar -Recurse
    Copy-Item -LiteralPath "$repo/artifacts/toolbar-validation/sdk-build.json" -Destination $toolbar
    $runtime = Join-Path $SdkRoot 'SimConnect SDK/lib/SimConnect.dll'
    $runtimeReport = Join-Path $output 'runtime-check.json'
    & "$repo/scripts/build/Test-SimConnectRuntime.ps1" -RuntimePath $runtime -ReportPath $runtimeReport
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $app 'SimConnect.dll')
    python -c "import sys,json;from pathlib import Path;sys.path.insert(0,'scripts/release');from package_release import validate_runtime;r,a=validate_runtime(*map(Path,sys.argv[1:4]));Path(sys.argv[4]).write_text(json.dumps(a,indent=2),encoding='utf-8')" $app $runtimeReport $RuntimeApprovalRecord (Join-Path $app 'RUNTIME-AUTHORIZATION.json')
    if ($LASTEXITCODE -ne 0) { throw 'Official runtime/ABI/authorization validation failed. No private candidate is approved.' }
    foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $app }
    Copy-Item -LiteralPath "$repo/licenses" -Destination $app -Recurse -Force
    @{ version='0.5.0'; profile='validation'; source_commit=$revision; source_dirty=$false; target='win-x64'; built_utc=[DateTime]::UtcNow.ToString('o'); public_scientific_gate_passed=$gatePassed; public_release_approved=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app 'build-info.json') -Encoding utf8
    Copy-Item -LiteralPath "$repo/docs/v0.5.0-private-validation.md" -Destination (Join-Path $app 'START-HERE.md')
    python scripts/release/private_validation.py record --folder $app --commit $revision
    if ($LASTEXITCODE -ne 0) { throw 'Exact-source private payload verification failed.' }
    $zip = Join-Path $output ("AeroScience-v0.5.0-Validation-" + $revision.Substring(0,7) + '.zip')
    Compress-Archive -LiteralPath $app -DestinationPath $zip -CompressionLevel Optimal
    ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
    Write-Host "PRIVATE validation source: $revision"
    Write-Host "Launch: $app\AeroScience.exe"
    Write-Host "Private archive/checksum: $zip"
    Write-Host 'Production installation and public release were not changed. No installer was created. Windows/MSFS/iPad/Quest and protected NASA original-file acceptance remain NOT TESTED. Do not upload this private build to Releases.'
} finally { Pop-Location }
