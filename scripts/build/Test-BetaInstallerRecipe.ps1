#Requires -Version 7.0
[CmdletBinding()]
param([string]$InnoCompiler)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'The private installer recipe compiler check requires Windows. No fixture was created.'}
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
. "$repo/scripts/release/InnoSetup.ps1"
$compiler=Resolve-InnoSetupCompiler -InnoCompiler $InnoCompiler
$null=Get-InnoSetupCompilerVersion -CompilerPath $compiler
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('aeroscience-beta-recipe-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $fixture 'payload/application')) | Out-Null
Set-Content (Join-Path $fixture 'payload/application/LICENSE') 'STRUCTURAL COMPILER FIXTURE ONLY; NOT A DISTRIBUTION'
Set-Content (Join-Path $fixture 'payload/START-HERE.md') 'STRUCTURAL FIXTURE ONLY; NEVER INSTALL OR DISTRIBUTE'
Set-Content (Join-Path $fixture 'VC_redist.x64.exe') 'NOT A RUNTIME; NEVER EXECUTE THIS FIXTURE'
Write-Host "Structural compiler fixture: $fixture. Nothing is installed, executed or published."
foreach($version in '0.7.1-beta.2','0.7.0-beta.1'){
    $output=Join-Path $fixture $version
    & $compiler "/DPayloadDir=$fixture/payload" "/DVcRuntime=$fixture/VC_redist.x64.exe" "/DBetaVersion=$version" "/DOutputDirectory=$output" "$repo/scripts/installer/AeroScienceBeta.iss"
    if($LASTEXITCODE -ne 0){throw "Private $version recipe compilation failed. Keep $fixture for diagnostics."}
    $info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output "AeroScience-Setup-v$version.exe"))
    $metadata=ConvertFrom-InnoVersionInfo -VersionInfo $info
    $metadata.version=$version; $metadata.language=$info.Language
    $metadata | ConvertTo-Json | Tee-Object -FilePath (Join-Path $output 'STRUCTURAL-METADATA.json')
    if($version -eq '0.7.1-beta.2' -and ($metadata.product_version -ne $version -or $metadata.product_name -ne 'AeroScience 0.7.1-beta.2' -or $metadata.numeric_version -ne '0.7.1.2')){
        throw "Structural Beta 2 resource metadata differs; see $output/STRUCTURAL-METADATA.json. No installed or SDK acceptance is claimed."
    }
}
Write-Host 'PASS: both private recipe variants compile and Beta 2 version resources match. Structural fixtures only; never install or distribute these EXEs.'
