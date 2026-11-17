#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][long]$FullCiRunId,
    [Parameter(Mandatory)][string]$VcRuntimePath,
    [string]$SdkRoot='C:\MSFS 2024 SDK',
    [string]$RuntimeApprovalRecord='C:\AeroScience-Release-Records\runtime-approval.json',
    [switch]$RuntimeRedistributionConfirmed,
    [string]$InnoCompiler,
    [switch]$AllowUnsignedBeta
)
$ErrorActionPreference='Stop';$PSNativeCommandUseErrorActionPreference=$false
if(-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64'){throw 'Build on Windows x64 with the genuine MSFS 2024 SDK. Linux cannot produce installed acceptance.'}
$repo=(Resolve-Path "$PSScriptRoot/../..").Path;$output=[IO.Path]::GetFullPath($OutputDirectory)
$policy=Join-Path $repo 'scripts/release/beta_release_080.py'
$preflight=Join-Path ([IO.Path]::GetDirectoryName($output)) ('Beta080-preflight-'+[Guid]::NewGuid().ToString('N')+'.json')
Push-Location $repo
try {
    $argsList=@($policy,'preflight','--source',$ExpectedSourceCommit,'--output',$output,'--sdk',$SdkRoot,'--approval',$RuntimeApprovalRecord,'--report',$preflight)
    if($RuntimeRedistributionConfirmed){$argsList+='--confirmed'}
    python @argsList
    if($LASTEXITCODE -ne 0){throw 'Follow the named preflight blockers. No candidate/installer was created.'}
    . "$PSScriptRoot/InnoSetup.ps1"
    $compiler=Resolve-InnoSetupCompiler -InnoCompiler $InnoCompiler;$compilerVersion=Get-InnoSetupCompilerVersion -CompilerPath $compiler
    $vc=(Resolve-Path -LiteralPath $VcRuntimePath).Path;$signature=Get-AuthenticodeSignature -LiteralPath $vc;$vcVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($vc)
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation' -or $vcVersion.FileMajorPart -ne 14 -or $vcVersion.ProductName -notmatch 'Visual C\+\+' -or ($vcVersion.OriginalFilename -notmatch 'x64' -and $vcVersion.ProductName -notmatch 'x64')){throw 'Supply the actual signed Microsoft VC v14 x64 redistributable. No prerequisite is downloaded or substituted.'}
    [IO.Directory]::CreateDirectory($output)|Out-Null
    $logs=Join-Path $output 'Logs';[IO.Directory]::CreateDirectory($logs)|Out-Null
    Copy-Item -LiteralPath $preflight -Destination (Join-Path $output 'preflight.json')
    function Step([string]$Name,[string]$Tool,[string[]]$Arguments){
        Write-Host $Name;$log=Join-Path $logs ($Name+'.log')
        & $Tool @Arguments *> $log
        $exitCode=$LASTEXITCODE
        if($exitCode -ne 0){
            Get-Content -LiteralPath $log -Tail 40 | ForEach-Object {Write-Host $_}
            throw "$Name failed (exit $exitCode). Details: $log. Preserve this attempt/log; no result was promoted to Passed."
        }
    }
    Step 'Exact-Full-CI' 'python' @($policy,'capture-ci','--source',$ExpectedSourceCommit,'--run-id',[string]$FullCiRunId,'--report',(Join-Path $output 'full-ci.json'))
    Step 'Locked-JavaScript' 'npm' @('ci','--ignore-scripts')
    Step 'Canonical-Locales' 'node' @('scripts/build/build-locales.mjs','--check')
    $destination=Join-Path $output 'installer-candidate';$payload=Join-Path $destination 'payload';$app=Join-Path $payload 'application'
    Step 'Publish-Beta-Windows' 'pwsh' @('-NoProfile','-File',"$repo/scripts/build/Publish-080BetaCompanion.ps1",'-ExpectedSourceCommit',$ExpectedSourceCommit,'-ArtifactsPath',(Join-Path $output 'Intermediate'),'-OutputDirectory',$app)
    $stage=Join-Path $output 'SDK-Stage';Step 'Stage-Beta-Toolbar' 'python' @($policy,'stage-toolbar','--folder',$stage,'--source',$ExpectedSourceCommit)
    $include=Join-Path $SdkRoot 'SimConnect SDK/include';$builder=Join-Path $SdkRoot 'Tools/bin/fspackagetool.exe'
    $proof=Get-Content $preflight -Raw|ConvertFrom-Json;$sdkProof=($proof.checks|Where-Object name -eq 'genuine-SDK-inputs').evidence
    function Assert-SdkInputs {
        foreach($pair in @(@{path=$builder;sha=$sdkProof.'fspackagetool.exe'},@{path=(Join-Path $include 'SimConnect.h');sha=$sdkProof.'SimConnect.h'})){
            if((Get-FileHash -LiteralPath $pair.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pair.sha){throw 'SDK inputs changed after preflight.'}
        }
    }
    Assert-SdkInputs
    Step 'SDK-ABI' 'cl.exe' @('/nologo','/c','/EHsc','/std:c++17',"/I$include","$repo/scripts/build/sdk-contract.cpp","/Fo$stage/sdk-contract.obj")
    Step 'Native-ES2017' 'node' @('scripts/build/native-javascript.mjs',$stage)
    if(Get-Process -Name FlightSimulator2024,FlightSimulator,fspackagetool -ErrorAction SilentlyContinue){throw 'Close MSFS/package builder yourself; preserve this attempt. No process was stopped.'}
    Push-Location $stage
    try{Step 'Genuine-SDK-Package' $builder @((Join-Path $stage 'AeroScience.xml'),'-outputdir',(Join-Path $stage 'Output'),'-tempdir',(Join-Path $stage '_PackageInt'),'-nopause')}finally{Pop-Location}
    Assert-SdkInputs
    $packages=@(Get-ChildItem (Join-Path $stage 'Output') -File -Filter manifest.json -Recurse|Where-Object {$_.Directory.Name -eq 'aeroscience-beta-v0-8-0-toolbar'})
    if($packages.Count -ne 1){throw 'Expected one genuine SDK Beta toolbar; no placeholder SPB is accepted.'}
    $toolbar=Join-Path $app 'Toolbar';[IO.Directory]::CreateDirectory($toolbar)|Out-Null;Copy-Item $packages[0].Directory.FullName -Destination $toolbar -Recurse
    $hashes=@{};Get-ChildItem $packages[0].Directory.FullName -Recurse -File|ForEach-Object {$hashes[[IO.Path]::GetRelativePath($packages[0].Directory.FullName,$_.FullName).Replace('\','/')]=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    @{variant='toolbar-beta-080';version='0.8.0-beta.1';source_commit=$ExpectedSourceCommit;source_dirty=$false;sdk_contract='passed';builder_sha256=$sdkProof.'fspackagetool.exe';sdk_header_sha256=$sdkProof.'SimConnect.h';native_javascript=(Get-Content (Join-Path $stage 'native-javascript.json') -Raw|ConvertFrom-Json);files=$hashes;built_utc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 12|Set-Content (Join-Path $toolbar 'sdk-build.json') -Encoding utf8
    $runtimeProof=($proof.checks|Where-Object name -eq 'native-runtime-loading-exports-authorization').evidence
    $runtime=Join-Path $SdkRoot 'SimConnect SDK/lib/SimConnect.dll';if((Get-FileHash $runtime -Algorithm SHA256).Hash.ToLowerInvariant() -ne $runtimeProof.report.sha256){throw 'Runtime changed after authorization/load/export verification.'}
    Copy-Item $runtime -Destination (Join-Path $app 'SimConnect.dll')
    $publicRuntime=[ordered]@{};foreach($key in @('checked_utc','sha256','file_version','architecture','export_check','exports')){if($runtimeProof.report.PSObject.Properties[$key]){$publicRuntime[$key]=$runtimeProof.report.$key}}
    $publicRuntime|ConvertTo-Json -Depth 10|Set-Content (Join-Path $app 'runtime-check.json') -Encoding utf8
    $runtimeProof.public_authorization|ConvertTo-Json -Depth 10|Set-Content (Join-Path $app 'RUNTIME-AUTHORIZATION.json') -Encoding utf8
    $signingPolicy=if($AllowUnsignedBeta){'allow-unsigned-beta'}else{'require-signed'};$dependencies=@{}
    foreach($relative in @('AeroScience.exe','SimConnect.dll','LidarRuntime/AeroScience.LidarReader.exe')){
        $file=Join-Path $app $relative;$sig=Get-AuthenticodeSignature $file;$names=@()
        if($relative -ne 'AeroScience.exe'){
            $imports=& dumpbin.exe /DEPENDENTS $file;if($LASTEXITCODE -ne 0){throw 'Native dependency inspection failed.'}
            $names=@($imports|ForEach-Object {if($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$'){$Matches[1].ToLowerInvariant()}}|Sort-Object -Unique)
        }
        $dependencies[$relative]=@{sha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant();imports=$names;signature_status=[string]$sig.Status;signer=if($sig.SignerCertificate){$sig.SignerCertificate.Subject}else{$null}}
    }
    @{schema='aeroscience-beta080-native-1';signing_policy=$signingPolicy;files=$dependencies}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $app 'native-dependencies.json') -Encoding utf8
    foreach($name in @('LICENSE','THIRD-PARTY-NOTICES.md')){Copy-Item (Join-Path $repo $name) -Destination $app}
    Copy-Item (Join-Path $repo 'licenses') -Destination $app -Recurse -Force
    @{profile='beta080';version='0.8.0-beta.1';channel='beta';source_commit=$ExpectedSourceCommit;source_dirty=$false;target='win-x64';stable_release_approved=$false}|ConvertTo-Json|Set-Content (Join-Path $app 'build-info.json') -Encoding utf8
    foreach($name in @('START-HERE.html','INSTALL-NOTES.txt','KNOWN-ISSUES.md','TEST-CARDS.md','RELEASE-NOTES.md')){Copy-Item (Join-Path $repo "docs/beta/v0.8.0-beta.1/$name") -Destination (Join-Path $payload $name)}
    Step 'Verify-And-Seal-Beta-Payload' 'python' @($policy,'seal','--folder',$app,'--source',$ExpectedSourceCommit)
    Step 'Compile-Installer' $compiler @("/DPayloadDir=$payload","/DVcRuntime=$vc","/DOutputDirectory=$destination","$repo/scripts/installer/AeroScienceBeta080.iss")
    $setup=Join-Path $destination 'AeroScience-Setup-v0.8.0-beta.1.exe';$setupSignature=Get-AuthenticodeSignature $setup
    $tools=Join-Path $output 'installer-tools.json'
    @{compiler_sha256=(Get-FileHash $compiler -Algorithm SHA256).Hash.ToLowerInvariant();compiler_version=$compilerVersion.ToString();vc_sha256=(Get-FileHash $vc -Algorithm SHA256).Hash.ToLowerInvariant();vc_version=$vcVersion.FileVersion;vc_signature=[string]$signature.Status;vc_publisher=$signature.SignerCertificate.Subject;vc_architecture='x64';setup_sha256=(Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant();setup_signature=[string]$setupSignature.Status}|ConvertTo-Json|Set-Content $tools -Encoding utf8
    Step 'Installer-Receipt-Checksum' 'python' @($policy,'record-installer','--folder',$destination,'--source',$ExpectedSourceCommit,'--compiler',$compiler,'--vc',$vc,'--tool-report',$tools)
    Step 'Pre-Beta-Readiness' 'python' @($policy,'readiness','--folder',$destination,'--source',$ExpectedSourceCommit,'--ci',(Join-Path $output 'full-ci.json'),'--report',(Join-Path $output 'readiness.json'))
    Write-Host "Candidate: $setup"
    Write-Host 'Built and verified; NOT approved for distribution yet. Test these exact installer bytes using the separate basic Beta checklist. No installation, publication or stable-release approval occurred.'
}catch{Write-Host "Keep $output, logs and receipts. Use a fresh Beta080-* folder for any rebuild. No earlier candidate/result was overwritten.";throw}finally{Pop-Location}
