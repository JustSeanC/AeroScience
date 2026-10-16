#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SdkRoot,
    [switch]$ProofOfConcept,
    [switch]$WindowProbe,
    [string]$PackageToolPath,
    [string]$HeaderDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($ProofOfConcept -and $WindowProbe) { throw 'Choose either -ProofOfConcept or -WindowProbe, not both.' }
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$SdkRoot = (Resolve-Path -LiteralPath $SdkRoot).Path
$tool = if ($PackageToolPath) { [IO.Path]::GetFullPath($PackageToolPath) } else { Join-Path $SdkRoot 'Tools/bin/fspackagetool.exe' }
$include = if ($HeaderDirectory) { [IO.Path]::GetFullPath($HeaderDirectory) } else { Join-Path $SdkRoot 'SimConnect SDK/include' }
if (-not (Test-Path $tool)) { throw "Official SDK package builder missing: $tool" }
if (-not (Test-Path "$include/SimConnect.h")) { throw 'Official SimConnect headers are missing.' }
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'Run from an x64 Visual Studio Native Tools shell (cl.exe is required for the SDK contract check).' }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3 must be installed and available as python on PATH for package validation.' }
$variant = if ($ProofOfConcept) { 'toolbar-poc' } elseif ($WindowProbe) { 'toolbar-window-probe' } else { 'toolbar-full' }
$stage = Join-Path $repo "artifacts/$variant"
# This is an isolated generated build directory. No SDK or checkout sources are modified.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item $stage -ItemType Directory -Force | Out-Null
$contractLog = Join-Path $stage 'sdk-contract.log'
& cl.exe /nologo /c /EHsc /std:c++17 "/I$include" "$PSScriptRoot/sdk-contract.cpp" "/Fo$stage/sdk-contract.obj" 2>&1 | Tee-Object -FilePath $contractLog
$compileExit = $LASTEXITCODE
if ($compileExit -ne 0) { throw "Installed SDK differs from the native ABI assumptions or compiler prerequisites failed (exit $compileExit). Inspect sdk-contract.log before packaging." }
Copy-Item "$repo/msfs-toolbar/*" $stage -Recurse
if ($ProofOfConcept -or $WindowProbe) {
    $definition = Join-Path $stage 'PackageSources/InGamePanels/aeroscience_panel.xml'
    $entryPage = if ($ProofOfConcept) { 'ProofOfConcept.html' } else { 'WindowProbe.html' }
    (Get-Content $definition -Raw).Replace('AeroScience/AeroScience.html', "AeroScience/$entryPage") | Set-Content $definition -Encoding utf8
}
$output = Join-Path $stage 'output'
Push-Location $stage
try {
    & $tool (Join-Path $stage 'AeroScience.xml') -outputdir $output -tempdir (Join-Path $stage '_PackageInt') -nopause 2>&1 | Tee-Object -FilePath (Join-Path $stage 'build.log')
    $packageExit = $LASTEXITCODE
    if ($packageExit -ne 0) { throw "Official SDK package build failed (exit $packageExit). Inspect build.log in the staged build directory." }
} finally { Pop-Location }
$packages = @(Get-ChildItem $output -Filter manifest.json -Recurse | Where-Object { $_.Directory.Name -eq 'aeroscience-toolbar' })
if ($packages.Count -ne 1) { throw 'Expected one SDK-built aeroscience-toolbar package.' }
$package = $packages[0].Directory.FullName
python "$repo/scripts/release/package_release.py" validate --community $package
if ($LASTEXITCODE -ne 0) { throw 'SDK output failed package validation.' }
$hashes = @{}
Get-ChildItem $package -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    $hashes[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{ variant=$variant; builder_sha256=(Get-FileHash $tool -Algorithm SHA256).Hash;
    sdk_header_sha256=(Get-FileHash (Join-Path $include 'SimConnect.h') -Algorithm SHA256).Hash;
    sdk_contract='passed'; built_utc=[DateTime]::UtcNow.ToString('o'); files=$hashes } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'sdk-build.json') -Encoding utf8
Write-Host "SDK-built output: $package. This build does not establish simulator, VR, or device acceptance. Complete docs/verification.md."
