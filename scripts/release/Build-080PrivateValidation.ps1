#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [string]$RuntimeApprovalRecord = 'C:\AeroScience-Release-Records\runtime-approval.json',
    [switch]$RuntimeRedistributionConfirmed
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Build locally on Windows x64 in an x64 Native Tools PowerShell 7 shell. Linux preparation cannot produce a genuine native package.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$preflight = Join-Path ([IO.Path]::GetDirectoryName($output)) ('Preflight080-' + [Guid]::NewGuid().ToString('N') + '.json')
& "$repo/scripts/build/Test-080NativeEnvironment.ps1" -ExpectedSourceCommit $ExpectedSourceCommit -OutputDirectory $output -SdkRoot $SdkRoot `
    -RuntimeApprovalRecord $RuntimeApprovalRecord -RuntimeRedistributionConfirmed:$RuntimeRedistributionConfirmed -ReportPath $preflight
$instance = [Guid]::NewGuid().ToString('N')
$policy = Join-Path $repo 'scripts/release/private_validation_080.py'
& python $policy begin --source $ExpectedSourceCommit --instance $instance --sdk $SdkRoot --output $output --report $preflight
if ($LASTEXITCODE -ne 0) { throw 'Fresh attempt creation refused. Previous files were preserved.' }
$app = Join-Path $output 'AeroScience-0.8.0-Validation'
$logs = Join-Path $output 'Logs'
[IO.Directory]::CreateDirectory($logs) | Out-Null
function Invoke-080Step([string]$StepName, [string]$ToolPath, [string[]]$ToolArguments) {
    Write-Host $StepName
    & $ToolPath @ToolArguments *> (Join-Path $logs ($StepName + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "$StepName failed. Preserve this attempt and inspect its log; no installer or native acceptance was produced." }
}
$intermediate = Join-Path $output 'Intermediate'
Push-Location $repo
try {
    Invoke-080Step 'Locked-JavaScript' 'npm' @('ci', '--ignore-scripts')
    Invoke-080Step 'Canonical-Locales' 'node' @('scripts/build/build-locales.mjs', '--check')
    Invoke-080Step 'Publish-Private-Windows-x64' 'pwsh' @('-NoProfile','-File',"$repo/scripts/build/Publish-080ValidationCompanion.ps1",'-ExpectedSourceCommit',$ExpectedSourceCommit,'-Instance',$instance,'-ArtifactsPath',$intermediate,'-OutputDirectory',$app)
    $stage = Join-Path $output 'SDK-Stage'
    Invoke-080Step 'Stage-Native-Toolbar' 'python' @($policy, 'stage-toolbar', '--folder', $stage, '--source', $ExpectedSourceCommit, '--instance', $instance)
    $include = Join-Path $SdkRoot 'SimConnect SDK/include'
    $builder = Join-Path $SdkRoot 'Tools/bin/fspackagetool.exe'
    $preflightProof = Get-Content -LiteralPath $preflight -Raw | ConvertFrom-Json
    $sdkInputs = ($preflightProof.checks | Where-Object name -eq 'genuine-SDK-inputs').evidence
    function Assert-080SdkInputs {
        foreach($entry in @(@{path=$builder;hash=$sdkInputs.'fspackagetool.exe'},@{path=(Join-Path $include 'SimConnect.h');hash=$sdkInputs.'SimConnect.h'})){
            if((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.hash){throw 'SDK builder/header changed after preflight. Preserve this attempt; no package receipt is approved.'}
        }
    }
    Assert-080SdkInputs
    Invoke-080Step 'SDK-ABI-Contract' 'cl.exe' @('/nologo', '/c', '/EHsc', '/std:c++17', "/I$include", "$repo/scripts/build/sdk-contract.cpp", "/Fo$stage/sdk-contract.obj")
    Invoke-080Step 'Native-ES2017' 'node' @('scripts/build/native-javascript.mjs', $stage)
    # Preflight requires MSFS closed, preventing fspackagetool attaching and waiting indefinitely.
    if (Get-Process -Name FlightSimulator2024,FlightSimulator,fspackagetool -ErrorAction SilentlyContinue) { throw 'Close MSFS/package builder yourself before continuing with a fresh attempt. No processes were stopped.' }
    $sdkOutput = Join-Path $stage 'Output'
    Push-Location $stage
    try { Invoke-080Step 'Genuine-SDK-Package' $builder @((Join-Path $stage 'AeroScience.xml'), '-outputdir', $sdkOutput, '-tempdir', (Join-Path $stage '_PackageInt'), '-nopause') }
    finally { Pop-Location }
    Assert-080SdkInputs
    $packages = @(Get-ChildItem -LiteralPath $sdkOutput -Filter manifest.json -File -Recurse | Where-Object { $_.Directory.Name -eq 'aeroscience-validation-v0-8-0-toolbar' })
    if ($packages.Count -ne 1) { throw 'Expected one genuine complete 0.8.0 SDK toolbar. No placeholder output is accepted.' }
    $toolbar = Join-Path $app 'Toolbar'
    [IO.Directory]::CreateDirectory($toolbar) | Out-Null
    Copy-Item -LiteralPath $packages[0].Directory.FullName -Destination $toolbar -Recurse
    $package = Join-Path $toolbar 'aeroscience-validation-v0-8-0-toolbar'
    $hashes = @{}
    Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
        $hashes[[IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    @{variant='toolbar-validation-080'; source_commit=$ExpectedSourceCommit; instance=$instance; source_dirty=$false; sdk_contract='passed';
        builder_sha256=(Get-FileHash -LiteralPath $builder -Algorithm SHA256).Hash.ToLowerInvariant();
        sdk_header_sha256=(Get-FileHash -LiteralPath (Join-Path $include 'SimConnect.h') -Algorithm SHA256).Hash.ToLowerInvariant();
        native_javascript=(Get-Content (Join-Path $stage 'native-javascript.json') -Raw | ConvertFrom-Json); files=$hashes;
        built_utc=[DateTime]::UtcNow.ToString('o'); installed_acceptance='Not Tested'} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $toolbar 'sdk-build.json') -Encoding utf8
    $proof = Get-Content -LiteralPath $preflight -Raw | ConvertFrom-Json
    $runtimeProof = ($proof.checks | Where-Object name -eq 'native-runtime-loading-exports-authorization').evidence
    $runtime = Join-Path $SdkRoot 'SimConnect SDK/lib/SimConnect.dll'
    if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash.ToLowerInvariant() -ne $runtimeProof.report.sha256) { throw 'Native runtime changed after preflight. Preserve this attempt.' }
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $app 'SimConnect.dll')
    $runtimeProof.report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $app 'runtime-check.json') -Encoding utf8
    $runtimeProof.public_authorization | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $app 'RUNTIME-AUTHORIZATION.json') -Encoding utf8
    $dependencies = @{}
    foreach ($relative in @('SimConnect.dll', 'LidarRuntime/AeroScience.LidarReader.exe')) {
        $path = Join-Path $app $relative
        $imports = & dumpbin.exe /DEPENDENTS $path
        if ($LASTEXITCODE -ne 0) { throw 'Native dependency inventory failed.' }
        $names = @($imports | ForEach-Object { if ($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') { $Matches[1] } } | Sort-Object -Unique)
        if ($names.Count -eq 0) { throw 'No native dependency names were inspected.' }
        $dependencies[$relative] = @{sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); imports=$names; signature_status=[string](Get-AuthenticodeSignature -LiteralPath $path).Status}
    }
    @{format_version=1; files=$dependencies; public_signing_approval=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $app 'native-dependencies.json') -Encoding utf8
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $app }
    # Publishing already creates licenses/ for engine dependencies. Merge the
    # canonical notices into this fresh build output instead of recreating it.
    Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination $app -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repo 'docs/roadmap/v0.8.0-p08b-owner-checklist.md') -Destination (Join-Path $app 'START-HERE.md')
    @{profile='validation'; version='0.8.0'; source_commit=$ExpectedSourceCommit; source_dirty=$false; instance=$instance; target='win-x64'; public_release_approved=$false; installed_acceptance='Not Tested'; built_utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app 'build-info.json') -Encoding utf8
    & python $policy template --source $ExpectedSourceCommit --instance $instance | Set-Content -LiteralPath (Join-Path $app 'ACCEPTANCE-TEMPLATE.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance template generation failed.' }
    Invoke-080Step 'Source-Still-Clean' 'python' @($policy, 'source-check', '--source', $ExpectedSourceCommit)
    Invoke-080Step 'Verify-Private-Payload' 'python' @($policy, 'record', '--folder', $app, '--source', $ExpectedSourceCommit, '--instance', $instance)
    $receiptHash = (Get-FileHash -LiteralPath (Join-Path $app 'VALIDATION-RECEIPT.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $zip = Join-Path $output ('AeroScience-v0.8.0-PrivateValidation-'+$ExpectedSourceCommit.Substring(0,7)+'-'+$instance.Substring(0,8)+'.zip')
    Compress-Archive -LiteralPath $app -DestinationPath $zip -CompressionLevel Optimal
    $archiveHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    ($archiveHash+'  '+[IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath ($zip+'.sha256') -Encoding ascii
    ($receiptHash+'  VALIDATION-RECEIPT.json') | Set-Content -LiteralPath (Join-Path $output 'receipt.sha256') -Encoding ascii
    @{source_commit=$ExpectedSourceCommit; instance=$instance; archive_sha256=$archiveHash; receipt_sha256=$receiptHash; completed_utc=[DateTime]::UtcNow.ToString('o'); installed_acceptance='Not Tested'; public_release_approved=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'BUILD-COMPLETE.json') -Encoding utf8
    Write-Host "Private source: $ExpectedSourceCommit | attempt: $instance"
    Write-Host "Package: $app"
    Write-Host "Archive: $zip | SHA-256: $archiveHash"
    Write-Host "Receipt SHA-256: $receiptHash"
    Write-Host 'Next: Verify-080PrivateValidation.ps1, then explicit Start-080Validation.ps1 and owner checklist. No toolbar was installed, no app was launched, no public gate was relaxed. Installed acceptance remains Not Tested.'
} catch {
    Write-Host "Build stopped. Keep $output and its logs. Choose a fresh Validation080-* attempt; do not delete prior output or reuse a partial receipt."
    throw
} finally { Pop-Location }
