#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$ArtifactsPath,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop';$PSNativeCommandUseErrorActionPreference=$false
if(-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64'){throw 'Beta publication requires Windows x64; this helper alone does not create a genuine SDK candidate.'}
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    python scripts/release/private_validation_080.py source-check --source $ExpectedSourceCommit
    if($LASTEXITCODE -ne 0){throw 'Exact clean source required.'}
    # The publisher can only use new sibling trees in an explicit beta build
    # attempt or the CI-only structural lane; never an installed application.
    $env:AS_BETA_APP_OUTPUT=[IO.Path]::GetFullPath($OutputDirectory);$env:AS_BETA_INTERMEDIATE=[IO.Path]::GetFullPath($ArtifactsPath)
    @'
import os,sys
from pathlib import Path
sys.path.insert(0,'scripts/release')
import private_validation_080 as v
paths=[v.no_links(os.environ[n]) for n in ['AS_BETA_APP_OUTPUT','AS_BETA_INTERMEDIATE']]
for p in paths:
    v.require(not p.exists(),'Preserve prior outputs; choose new beta trees.')
    for root in v.protected_roots():v.require(not v.overlap(p,root),'Output overlaps installed data/application.')
    ci=Path.cwd()/'artifacts/beta080-structural'
    v.require(any(a.name.startswith('Beta080-') for a in p.parents) or p.is_relative_to(ci),'Use the Beta080 attempt or explicit CI structural lane.')
v.require(not v.overlap(*paths),'App and intermediate outputs must be separate.')
'@ | python -
    if($LASTEXITCODE -ne 0){throw 'Publication output guard refused.'}
    $native=Join-Path $ArtifactsPath 'native-lidar'
    $properties=@('-p:AeroScienceBeta080=true',"-p:SourceRevisionId=$ExpectedSourceCommit","-p:LidarBuildDirectory=$native")
    dotnet restore AeroScience.sln --locked-mode --artifacts-path $ArtifactsPath @properties
    if($LASTEXITCODE -ne 0){throw 'Beta locked restore failed; do not regenerate locks.'}
    dotnet publish src/AeroScience.Desktop -c Release -r win-x64 --self-contained true --no-restore --artifacts-path $ArtifactsPath @properties -o $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'Beta Windows publication failed.'}
    foreach($name in 'AeroScience.Engine.exe','createdump.exe'){$file=Join-Path $OutputDirectory $name;if(Test-Path $file){Remove-Item -LiteralPath $file}}
    Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse -Filter *.pdb | Remove-Item
    python scripts/build/verify_desktop_branding.py --folder $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'Compiled Windows branding failed.'}
    $probe=& dotnet run --project tests/AeroScience.TestHost -c Release --no-restore --artifacts-path $ArtifactsPath @properties -- --profile-probe-beta080 (Join-Path $OutputDirectory 'AeroScience.Engine.dll') $ExpectedSourceCommit
    if($LASTEXITCODE -ne 0){throw 'Actual Beta assembly/profile/native/locale identity failed.'}
    $lines=@($probe|Where-Object {$_.StartsWith('{') -and $_.Contains('configuration_directory')})
    if($lines.Count -ne 1){throw 'Expected one compiled Beta profile proof.'}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'compiled-profile.json'),$lines[0],[Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $native 'build.json') -Destination (Join-Path $OutputDirectory 'LidarRuntime/build.json')
    Copy-Item -LiteralPath (Join-Path $repo 'native/lidar/source-policy.json') -Destination (Join-Path $OutputDirectory 'LidarRuntime/source-policy.json')
    Copy-Item -LiteralPath (Join-Path $repo 'native/lidar/laszip/AUTHORS.txt') -Destination (Join-Path $OutputDirectory 'LidarRuntime/AUTHORS.txt')
    $decode=& python scripts/release/private_validation_080.py decoder-probe --folder (Join-Path $OutputDirectory 'LidarRuntime/AeroScience.LidarReader.exe')
    if($LASTEXITCODE -ne 0){throw 'Genuine Windows LAZ operation failed.'}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'lidar-check.json'),($decode -join "`n"),[Text.UTF8Encoding]::new($false))
    git diff --exit-code -- '**/packages.lock.json'
    if($LASTEXITCODE -ne 0){throw 'Beta graph changed dependency locks.'}
    Write-Host 'Beta Windows metadata, self-contained graph and genuine LAZ decode verified. SDK/installer/installed acceptance still Not Tested.'
}finally{Remove-Item Env:AS_BETA_APP_OUTPUT,Env:AS_BETA_INTERMEDIATE -ErrorAction SilentlyContinue;Pop-Location}
