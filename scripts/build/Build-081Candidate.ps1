#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExpectedSourceCommit,[Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^[0-9a-f]{32}$')][string]$Instance=([Guid]::NewGuid().ToString('N')),
    [string]$SdkRoot,[string]$RuntimeApprovalRecord,[switch]$RuntimeRedistributionConfirmed)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
if(-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64){throw 'Candidate publication requires Windows x64. No output was created.'}
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
$policy=Join-Path $PSScriptRoot 'candidate_081.py'
foreach($command in 'git','dotnet','node','npm','python'){if(-not(Get-Command $command -ErrorAction SilentlyContinue)){throw "Required build tool: $command"}}
if(-not(Get-Command cl.exe -ErrorAction SilentlyContinue) -or -not(Get-Command dumpbin.exe -ErrorAction SilentlyContinue) -or $env:VSCMD_ARG_TGT_ARCH -ne 'x64'){
    $toolchain=& python -c 'import sys,os,json,subprocess;from pathlib import Path;sys.path.insert(0,str(Path(sys.argv[1])/"scripts/build"));from lidar_runtime import msvc_environment;locator=Path(os.environ["ProgramFiles(x86)"])/"Microsoft Visual Studio/Installer/vswhere.exe";vs=subprocess.check_output([str(locator),"-latest","-products","*","-requires","Microsoft.VisualStudio.Component.VC.Tools.x86.x64","-property","installationPath"],text=True).strip();env=msvc_environment(Path(vs)/"Common7/Tools/VsDevCmd.bat");print(json.dumps({k:env[k] for k in ("PATH","INCLUDE","LIB","LIBPATH","VSCMD_ARG_TGT_ARCH") if k in env}))' $repo
    if($LASTEXITCODE -ne 0){throw 'Installed Visual Studio x64 C++ tools could not be initialized; no compiler or runtime is downloaded.'}
    $paths=$toolchain|ConvertFrom-Json
    foreach($entry in $paths.PSObject.Properties){[Environment]::SetEnvironmentVariable($entry.Name,[string]$entry.Value,'Process')}
}
if($SdkRoot){
    $SdkRoot=(Resolve-Path -LiteralPath $SdkRoot).Path
    if(-not $RuntimeRedistributionConfirmed -or -not $RuntimeApprovalRecord){throw 'SDK/runtime inclusion requires the existing explicit hash-bound redistribution approval record and confirmation.'}
    foreach($name in 'cl.exe','dumpbin.exe'){if(-not(Get-Command $name -ErrorAction SilentlyContinue)){throw 'Use the x64 Visual Studio Native Tools shell for SDK publication.'}}
    if(Get-Process -Name FlightSimulator2024,FlightSimulator,fspackagetool -ErrorAction SilentlyContinue){throw 'Close MSFS and the previous package builder yourself before building; no process was stopped.'}
}
& python $policy prepare --folder $OutputDirectory --source $ExpectedSourceCommit --instance $Instance
if($LASTEXITCODE -ne 0){throw 'Candidate source/path preflight refused. Earlier outputs were preserved.'}
$output=[IO.Path]::GetFullPath($OutputDirectory);$app=Join-Path $output 'application';$intermediate=Join-Path $output 'intermediate'
$logs=Join-Path $output 'Logs';[IO.Directory]::CreateDirectory($logs)|Out-Null
$native=Join-Path $intermediate 'native-lidar'
$properties=@('-p:AeroScienceCandidate081=true',"-p:AeroScienceCandidateInstance=$Instance","-p:SourceRevisionId=$ExpectedSourceCommit","-p:LidarBuildDirectory=$native")
function Checked([string]$Name,[scriptblock]$Run){& $Run 2>&1|Tee-Object -FilePath (Join-Path $logs ($Name+'.log'));if($LASTEXITCODE -ne 0){throw "$Name failed. Preserve the complete attempt; choose a fresh Candidate081-* folder for a rebuild."}}
Push-Location $repo
try{
    Checked 'Locked-Web-Dependencies' {npm ci --ignore-scripts}
    Checked 'Catalog-Identity' {node scripts/build/build-locales.mjs --check}
    Checked 'Locked-Restore' {dotnet restore AeroScience.sln --locked-mode --artifacts-path $intermediate @properties}
    Checked 'Windows-Publish' {dotnet publish src/AeroScience.Desktop -c Release -r win-x64 --self-contained true --no-restore --artifacts-path $intermediate @properties -o $app}
    foreach($name in 'AeroScience.Engine.exe','createdump.exe'){ $path=Join-Path $app $name;if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}
    Get-ChildItem -LiteralPath $app -Filter *.pdb -File -Recurse|Remove-Item
    Copy-Item -LiteralPath (Join-Path $native 'build.json') -Destination (Join-Path $app 'LidarRuntime/build.json')
    Checked 'Branding' {python scripts/build/verify_desktop_branding.py --folder $app}
    $proof=& dotnet run --project tests/AeroScience.TestHost -c Release --no-restore --artifacts-path $intermediate @properties -- --profile-probe-081 (Join-Path $app 'AeroScience.Engine.dll') $ExpectedSourceCommit $Instance
    if($LASTEXITCODE -ne 0){throw 'Actual published candidate metadata proof failed.'}
    $lines=@($proof|Where-Object {$_.StartsWith('{') -and $_.Contains('candidate081')})
    if($lines.Count -ne 1){throw 'Expected one compiled candidate metadata proof.'}
    [IO.File]::WriteAllText((Join-Path $app 'compiled-profile.json'),$lines[0],[Text.UTF8Encoding]::new($false))
    $hostFiles=@(Get-ChildItem (Join-Path $intermediate 'bin/AeroScience.TestHost') -Recurse -File -Filter AeroScience.TestHost.dll)
    if($hostFiles.Count -ne 1){throw 'Expected one isolated candidate test harness.'}
    $smoke=Join-Path $output 'smoke-harness';Copy-Item -LiteralPath $hostFiles[0].Directory.FullName -Destination $smoke -Recurse
    # Exercise the published runtime bytes, retaining test-only dependencies solely
    # in the disposable harness. Nothing from the harness enters application/.
    Get-ChildItem -LiteralPath $app|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $smoke -Recurse -Force}
    Checked 'Published-XPlane-Startup' {dotnet (Join-Path $smoke 'AeroScience.TestHost.dll') --candidate-startup-081 (Join-Path $app 'startup-no-simconnect.json')}
    Remove-Item -LiteralPath $smoke -Recurse -Force
    Checked 'Retained-LAZ' {python -c 'import sys,json;from pathlib import Path;sys.path.insert(0,"scripts/release");from private_validation_080 import decoder_probe;r=decoder_probe(Path(sys.argv[1]));Path(sys.argv[2]).write_text(json.dumps(r,indent=2))' (Join-Path $app 'LidarRuntime/AeroScience.LidarReader.exe') (Join-Path $app 'lidar-check.json')}
    $stage=Join-Path $output 'toolbar-source'
    Checked 'Toolbar-Stage' {python $policy stage-toolbar --folder $stage --source $ExpectedSourceCommit --instance $Instance}
    Checked 'Native-ES2017' {node scripts/build/native-javascript.mjs $stage}
    if($SdkRoot){
        $builder=Join-Path $SdkRoot 'Tools/bin/fspackagetool.exe';$include=Join-Path $SdkRoot 'SimConnect SDK/include'
        if(-not (Test-Path -LiteralPath $builder) -or -not (Test-Path -LiteralPath (Join-Path $include 'SimConnect.h'))){throw 'Official MSFS 2024 SDK builder/headers missing.'}
        Checked 'SDK-Contract' {cl.exe /nologo /c /EHsc /std:c++17 "/I$include" scripts/build/sdk-contract.cpp "/Fo$stage/sdk-contract.obj"}
        Checked 'SDK-Package' {& $builder (Join-Path $stage 'AeroScience.xml') -outputdir (Join-Path $stage 'output') -tempdir (Join-Path $stage '_PackageInt') -nopause}
        $packages=@(Get-ChildItem (Join-Path $stage 'output') -Recurse -Filter manifest.json|Where-Object {$_.Directory.Name -eq 'aeroscience-candidate-v0-8-1-toolbar'})
        if($packages.Count -ne 1){throw 'Expected one genuine candidate SDK package; placeholder SPB output is refused.'}
        $toolbar=Join-Path $app 'Toolbar';[IO.Directory]::CreateDirectory($toolbar)|Out-Null;Copy-Item -LiteralPath $packages[0].Directory.FullName -Destination $toolbar -Recurse
        $package=Join-Path $toolbar 'aeroscience-candidate-v0-8-1-toolbar';$files=@{}
        Get-ChildItem -LiteralPath $package -Recurse -File|ForEach-Object {$files[[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/')]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        @{variant='toolbar-candidate-081';source_commit=$ExpectedSourceCommit;instance=$Instance;source_dirty=$false;sdk_contract='passed';builder_sha256=(Get-FileHash -LiteralPath $builder -Algorithm SHA256).Hash.ToLowerInvariant();sdk_header_sha256=(Get-FileHash -LiteralPath (Join-Path $include 'SimConnect.h') -Algorithm SHA256).Hash.ToLowerInvariant();native_javascript=(Get-Content (Join-Path $stage 'native-javascript.json') -Raw|ConvertFrom-Json);files=$files}|ConvertTo-Json -Depth 12|Set-Content (Join-Path $output 'toolbar-sdk.json') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $output 'toolbar-sdk.json') -Destination (Join-Path $toolbar 'sdk-build.json')
        $runtime=Join-Path $SdkRoot 'SimConnect SDK/lib/SimConnect.dll';$runtimeCheck=Join-Path $app 'runtime-check.json'
        Checked 'Native-Runtime' {pwsh -NoProfile -File scripts/build/Test-SimConnectRuntime.ps1 -RuntimePath $runtime -ReportPath $runtimeCheck}
        $approvalOutput=Join-Path $app 'RUNTIME-AUTHORIZATION.json'
        Checked 'Runtime-Authorization' {python -c 'import sys,json;from pathlib import Path;sys.path.insert(0,"scripts/release");from package_release import validate_runtime;r,a=validate_runtime(Path(sys.argv[1]).parent,Path(sys.argv[2]),Path(sys.argv[3]));Path(sys.argv[4]).write_text(json.dumps(a,indent=2))' $runtime $runtimeCheck $RuntimeApprovalRecord $approvalOutput}
        Copy-Item -LiteralPath $runtime -Destination (Join-Path $app 'SimConnect.dll')
    }
    $imports=@{}
    foreach($relative in @('LidarRuntime/AeroScience.LidarReader.exe')+$(if($SdkRoot){@('SimConnect.dll')}else{@()})){
        $file=Join-Path $app $relative
        if(Get-Command dumpbin.exe -ErrorAction SilentlyContinue){
            $text=& dumpbin.exe /DEPENDENTS $file;if($LASTEXITCODE -ne 0){throw 'Native import inspection failed.'}
            $names=@($text|ForEach-Object {if($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$'){$Matches[1]}}|Sort-Object -Unique)
            if($names.Count -eq 0){throw 'Native import inspection returned no imports.'}
            $imports[$relative]=@{sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant();imports=$names;signature_status=[string](Get-AuthenticodeSignature -LiteralPath $file).Status}
        }else{throw 'Run from an x64 Visual Studio Native Tools shell so native imports can be inspected.'}
    }
    $imports|ConvertTo-Json -Depth 8|Set-Content (Join-Path $app 'native-dependencies.json') -Encoding utf8
    Checked 'Locks-Unchanged' {git diff --exit-code -- '**/packages.lock.json'}
    Checked 'Verify-And-Seal' {python $policy record --folder $output --source $ExpectedSourceCommit --instance $Instance}
    Write-Host "Private candidate prepared: $output. No app/toolbar installed or launched. Installed acceptance: Not Tested. SDK included: $([bool]$SdkRoot)."
}finally{Pop-Location}
