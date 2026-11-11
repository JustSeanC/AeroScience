#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [string]$ChartRuntimeDirectory,
    [ValidateSet('0.7.1-beta.2')][string]$PrivateBetaVersion
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    $profileArguments = @('-p:AeroScienceValidation=true')
    if ($PrivateBetaVersion) { $profileArguments += "-p:AeroSciencePrivateBetaVersion=$PrivateBetaVersion" }
    # Match Build-Companion: a solution restore honors each project's declared RIDs.
    # A publish-time restore with global -r win-x64 instead changes runtime-neutral
    # libraries' dependency graphs and fails NU1004 against their committed locks.
    # Use the SAME isolated artifacts/profile for restore, probe and publish.
    dotnet restore AeroScience.sln --locked-mode --artifacts-path $ArtifactsPath @profileArguments
    if ($LASTEXITCODE -ne 0) { throw 'Validation-profile locked dependency restore failed.' }
    dotnet run --project tests/AeroScience.TestHost -c $Configuration --no-restore --artifacts-path $ArtifactsPath @profileArguments -- --profile-probe
    if ($LASTEXITCODE -ne 0) { throw 'Compiled validation profile isolation failed.' }
    dotnet publish src/AeroScience.Desktop -c $Configuration -r win-x64 --self-contained true --no-restore --artifacts-path $ArtifactsPath @profileArguments -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Validation-profile Windows compilation failed.' }
    if ($PrivateBetaVersion) {
        $revision = (git rev-parse HEAD).Trim()
        $versions = @{}
        foreach ($name in 'AeroScience.dll','AeroScience.Engine.dll') {
            $binary = Join-Path $OutputDirectory $name
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
            if ($info.ProductVersion -ne "$PrivateBetaVersion+$revision" -or $info.FileVersion -ne '0.7.1.2') { throw "Published beta version/source differs: $name" }
            $versions[$name] = @{ product_version=$info.ProductVersion; file_version=$info.FileVersion; sha256=(Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        @{version=$PrivateBetaVersion; source_commit=$revision; binaries=$versions} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'PRIVATE-PROFILE.json') -Encoding utf8
    }
    # Desktop loads Engine.dll directly. Match production cleanup before a new
    # validation receipt is recorded; redundant apphosts cannot ship to testers.
    foreach ($buildExecutable in @('AeroScience.Engine.exe', 'createdump.exe')) {
        $path = Join-Path $OutputDirectory $buildExecutable
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
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
