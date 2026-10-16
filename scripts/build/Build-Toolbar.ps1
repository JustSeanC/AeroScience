#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SdkRoot, [switch]$ProofOfConcept)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$tool = Join-Path $SdkRoot 'Tools/bin/fspackagetool.exe'
$include = Join-Path $SdkRoot 'SimConnect SDK/include'
if (-not (Test-Path $tool)) { throw "Official SDK package builder missing: $tool" }
if (-not (Test-Path "$include/SimConnect.h")) { throw 'Official SimConnect headers are missing.' }
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'Run from an x64 Visual Studio Native Tools shell (cl.exe is required for the SDK contract check).' }
$variant = if ($ProofOfConcept) { 'toolbar-poc' } else { 'toolbar-full' }
$stage = Join-Path $repo "artifacts/$variant"
# This is an isolated generated build directory. No SDK or checkout sources are modified.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item $stage -ItemType Directory -Force | Out-Null
& cl.exe /nologo /c /EHsc "/I$include" "$PSScriptRoot/sdk-contract.cpp" "/Fo$stage/sdk-contract.obj"
if ($LASTEXITCODE -ne 0) { throw 'Installed SDK differs from the native ABI assumptions. Stop and update the integration before packaging.' }
Copy-Item "$repo/msfs-toolbar/*" $stage -Recurse
if ($ProofOfConcept) {
    $definition = Join-Path $stage 'PackageSources/InGamePanels/aeroscience_panel.xml'
    (Get-Content $definition -Raw).Replace('AeroScience/AeroScience.html', 'AeroScience/ProofOfConcept.html') | Set-Content $definition -Encoding utf8
}
$output = Join-Path $stage 'output'
& $tool (Join-Path $stage 'AeroScience.xml') -outputdir $output -tempdir (Join-Path $stage '_PackageInt') -nopause
if ($LASTEXITCODE -ne 0) { throw 'Official SDK package build failed. Inspect the build report.' }
$packages = @(Get-ChildItem $output -Filter manifest.json -Recurse | Where-Object { $_.Directory.Name -eq 'aeroscience-toolbar' })
if ($packages.Count -ne 1) { throw 'Expected one SDK-built aeroscience-toolbar package.' }
$package = $packages[0].Directory.FullName
python "$repo/scripts/release/package_release.py" validate --community $package
if ($LASTEXITCODE -ne 0) { throw 'SDK output failed package validation.' }
$hashes = @{}
Get-ChildItem $package -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
    $hashes[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{ variant=$variant; builder_sha256=(Get-FileHash $tool -Algorithm SHA256).Hash; built_utc=[DateTime]::UtcNow.ToString('o'); files=$hashes } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'sdk-build.json') -Encoding utf8
Write-Host "SDK-built output: $package. This build does not establish simulator, VR, or device acceptance. Complete docs/verification.md."
