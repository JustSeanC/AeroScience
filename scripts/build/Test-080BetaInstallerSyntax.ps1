#Requires -Version 7.0
# Compile-only structural fixture. NEVER launch, distribute or count as installed acceptance.
$ErrorActionPreference='Stop';$PSNativeCommandUseErrorActionPreference=$false
if(-not $IsWindows){throw 'The actual Inno compiler requires Windows.'}
. "$PSScriptRoot/../release/InnoSetup.ps1"
$compiler=Resolve-InnoSetupCompiler
$version=Get-InnoSetupCompilerVersion -CompilerPath $compiler
$fixture=Join-Path $env:RUNNER_TEMP ('beta080-inno-STRUCTURAL-'+[Guid]::NewGuid().ToString('N'))
New-Item (Join-Path $fixture 'application') -ItemType Directory|Out-Null
Set-Content (Join-Path $fixture 'application/LICENSE') 'SYNTHETIC COMPILER INPUT ONLY. NOT A DISTRIBUTION.'
Set-Content (Join-Path $fixture 'INSTALL-NOTES.txt') 'STRUCTURAL SYNTAX TEST. NO GENUINE APPLICATION, SDK OR RUNTIME.'
Set-Content (Join-Path $fixture 'VC_redist.x64.exe') 'NOT AN EXECUTABLE. NEVER INSTALL THIS FIXTURE.'
& $compiler "/DPayloadDir=$fixture" "/DVcRuntime=$fixture/VC_redist.x64.exe" "/DOutputDirectory=$fixture/output" "$PSScriptRoot/../installer/AeroScienceBeta080.iss"
if($LASTEXITCODE -ne 0){throw 'Beta installer Pascal/recipe syntax failed.'}
Write-Host "Beta recipe compiled with Inno $version. Structural syntax only; no installation, SDK or signing acceptance."
