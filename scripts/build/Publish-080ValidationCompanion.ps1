#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExpectedSourceCommit, [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$Instance,
    [Parameter(Mandatory)][string]$ArtifactsPath, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
if(-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64){throw 'Private Windows publishing requires Windows x64; this structural helper is not SDK/MSFS acceptance.'}
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
$policy=Join-Path $repo 'scripts/release/private_validation_080.py'
& python $policy source-check --source $ExpectedSourceCommit
if($LASTEXITCODE -ne 0){throw 'Pinned clean source verification failed.'}
& python $policy publish-paths --folder $OutputDirectory --output $ArtifactsPath
if($LASTEXITCODE -ne 0){throw 'Fresh isolated publication paths required; no existing app/data may be selected.'}
$native=Join-Path ([IO.Path]::GetFullPath($ArtifactsPath)) 'native-lidar'
# Keep the original NuGet project/package identity and locked graph. Only private
# assembly/file/product versions change; no lock regeneration or global RID restore.
$properties=@('-p:AeroScienceValidation=true','-p:AeroScienceValidationVersion=0.8.0',"-p:AeroScienceValidationInstance=$Instance",
    '-p:AssemblyVersion=0.8.0.0','-p:FileVersion=0.8.0.0','-p:InformationalVersion=0.8.0',"-p:SourceRevisionId=$ExpectedSourceCommit","-p:LidarBuildDirectory=$native")
Push-Location $repo
try{
    & dotnet restore AeroScience.sln --locked-mode --artifacts-path $ArtifactsPath @properties
    if($LASTEXITCODE -ne 0){throw 'Private locked restore failed; dependency locks were not regenerated.'}
    & dotnet publish src/AeroScience.Desktop -c Release -r win-x64 --self-contained true --no-restore --artifacts-path $ArtifactsPath @properties -o $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'Private Windows compilation failed.'}
    foreach($name in 'AeroScience.Engine.exe','createdump.exe'){
        $path=Join-Path $OutputDirectory $name
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
    }
    Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter *.pdb | Remove-Item
    & python scripts/build/verify_desktop_branding.py --folder $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'Compiled Windows branding check failed.'}
    $probe=& dotnet run --project tests/AeroScience.TestHost -c Release --no-restore --artifacts-path $ArtifactsPath @properties -- --profile-probe-080 (Join-Path $OutputDirectory 'AeroScience.Engine.dll') $ExpectedSourceCommit $Instance
    if($LASTEXITCODE -ne 0){throw 'Actual published Engine/Missions/presentation metadata did not match this private build.'}
    $lines=@($probe | Where-Object { $_.StartsWith('{') -and $_.Contains('configuration_directory') })
    if($lines.Count -ne 1){throw 'Expected one published private metadata proof.'}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'compiled-profile.json'),$lines[0],[Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $native 'build.json') -Destination (Join-Path $OutputDirectory 'LidarRuntime/build.json')
    $decoder=& python $policy decoder-probe --folder (Join-Path $OutputDirectory 'LidarRuntime/AeroScience.LidarReader.exe')
    if($LASTEXITCODE -ne 0){throw 'Actual Windows pinned decoder/genuine retained LAZ reference comparison failed.'}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'lidar-check.json'),($decoder -join "`n"),[Text.UTF8Encoding]::new($false))
    & git diff --exit-code -- '**/packages.lock.json'
    if($LASTEXITCODE -ne 0){throw 'Private publication changed a committed lock; preserve diagnostics and do not record a package receipt.'}
    Write-Host 'Private Windows structure/metadata and retained genuine LAZ decoding checked. No SDK toolbar/runtime, installed MSFS, owner-machine acceptance or public package is approved by this helper.'
}finally{Pop-Location}
