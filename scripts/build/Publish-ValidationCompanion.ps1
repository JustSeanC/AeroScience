#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [string]$ChartRuntimeDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    # Match Build-Companion: a solution restore honors each project's declared RIDs.
    # A publish-time restore with global -r win-x64 instead changes runtime-neutral
    # libraries' dependency graphs and fails NU1004 against their committed locks.
    # Use the SAME isolated artifacts/profile for restore, probe and publish.
    dotnet restore AeroScience.sln --locked-mode --artifacts-path $ArtifactsPath '-p:AeroScienceValidation=true'
    if ($LASTEXITCODE -ne 0) { throw 'Validation-profile locked dependency restore failed.' }
    dotnet run --project tests/AeroScience.TestHost -c $Configuration --no-restore --artifacts-path $ArtifactsPath '-p:AeroScienceValidation=true' -- --profile-probe
    if ($LASTEXITCODE -ne 0) { throw 'Compiled validation profile isolation failed.' }
    dotnet publish src/AeroScience.Desktop -c $Configuration -r win-x64 --self-contained true --no-restore --artifacts-path $ArtifactsPath '-p:AeroScienceValidation=true' -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Validation-profile Windows compilation failed.' }
    python scripts/build/verify_desktop_branding.py --folder $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Validation-profile Windows icon resources failed verification.' }
    if ($ChartRuntimeDirectory) {
        # Optional source-built 0.6.0 private runtime only. Public release builders do
        # not consume this parameter or gain an automatic redistribution approval.
        python scripts/build/chart_runtime.py verify --folder $ChartRuntimeDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Private chart runtime identity/data/import verification failed.' }
        $chartDestination = Join-Path $OutputDirectory 'ChartRuntime'
        if (Test-Path -LiteralPath $chartDestination) { throw 'Preserve the existing private chart runtime; choose a fresh output directory.' }
        Copy-Item -LiteralPath $ChartRuntimeDirectory -Destination $chartDestination -Recurse
        python scripts/build/chart_runtime.py verify --folder $chartDestination
        if ($LASTEXITCODE -ne 0) { throw 'Copied private chart runtime failed exact-byte verification.' }
    }
} finally { Pop-Location }
