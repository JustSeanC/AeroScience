#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Build')]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory,ParameterSetName='Build')][string]$RuntimeApprovalRecord,
    [Parameter(ParameterSetName='Build')][switch]$RuntimeRedistributionConfirmed,
    [Parameter(ParameterSetName='Build')][string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [Parameter(Mandatory,ParameterSetName='Reuse')][string]$ValidationFolder,
    [Parameter(Mandatory,ParameterSetName='Reuse')][string]$RuntimeReport,
    [string]$ExpectedSourceCommit,
    [string]$InnoCompiler,
    [string]$VcRuntimePath,
    [ValidateSet('0.7.1-beta.2')][string]$PrivateBetaVersion
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    if ($PrivateBetaVersion) { throw 'Build 0.7.1-beta.2 on Windows x64 with PowerShell 7, the genuine SDK and Inno Setup 6.3 or newer.' }
    throw 'Build Beta 1 on Windows x64 with PowerShell 7 and Inno Setup 6.3 or newer.'
}
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    $privateArguments = @(); $profileArguments = @{}
    if ($PrivateBetaVersion) { $privateArguments = @('--beta-version',$PrivateBetaVersion); $profileArguments.PrivateBetaVersion=$PrivateBetaVersion }
    $identity = (python scripts/release/private_validation.py identity @privateArguments) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Unsupported private beta installer identity.' }
    $revision = (git rev-parse HEAD).Trim()
    $branch = "$(git branch --show-current)".Trim()
    $sourceSelected = $branch -eq $identity.branch -or ($PrivateBetaVersion -and -not $branch -and $ExpectedSourceCommit -eq $revision)
    if ($LASTEXITCODE -ne 0 -or -not $sourceSelected -or (git status --porcelain)) { throw "Use clean $($identity.branch) source, or its explicit exact-source detached private-beta checkout. Preserve existing work." }
    if (-not $ExpectedSourceCommit) { $ExpectedSourceCommit = $revision }
    if ($ExpectedSourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'ExpectedSourceCommit must be the exact 40-character payload source SHA.' }
    if ($PSCmdlet.ParameterSetName -eq 'Build' -and $ExpectedSourceCommit -ne $revision) { throw 'A new build must use this checkout source SHA.' }
    if ($PrivateBetaVersion) {
        git merge-base --is-ancestor 331dbf20d9c443f088eb0f97de1363c4d7af26b7 HEAD
        if ($LASTEXITCODE -ne 0) { throw 'The accepted v0.7.1 maintenance source is required.' }
        $profileArguments.ExpectedSourceCommit=$revision
    }
    if (Get-Process -Name FlightSimulator2024,fspackagetool -ErrorAction SilentlyContinue) { throw "Close MSFS and finish any existing SDK build before packaging $($identity.title)." }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Preserve earlier candidates; choose a new nonexistent output directory.' }
    if ($PSCmdlet.ParameterSetName -eq 'Reuse') {
        $sourceBoundary = [IO.Path]::GetFullPath($ValidationFolder).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $outputBoundary = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($sourceBoundary.StartsWith($outputBoundary,[StringComparison]::OrdinalIgnoreCase) -or $outputBoundary.StartsWith($sourceBoundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Source validation and beta output must not overlap. Preserve the original validator.' }
    }
    . "$PSScriptRoot/InnoSetup.ps1"
    $compiler = Resolve-InnoSetupCompiler -InnoCompiler $InnoCompiler
    $compilerVersion = Get-InnoSetupCompilerVersion -CompilerPath $compiler
    # Protect stable/validation data and installed applications before creating output.
    $protectedRoots = @((Join-Path $env:LOCALAPPDATA 'AeroScience'),(Join-Path $env:LOCALAPPDATA 'Programs/AeroScience'),(Join-Path $env:LOCALAPPDATA 'Programs/AeroScience-Beta-v0.7.0-beta.1'))
    foreach ($version in '0.5.0','0.6.0','0.7.0') { $protectedRoots += Join-Path $env:LOCALAPPDATA "AeroScience-Validation-v$version" }
    $protectedRoots += @((Join-Path $env:LOCALAPPDATA 'Programs/AeroScience-Beta-v0.7.1-beta.2'),(Join-Path $env:LOCALAPPDATA 'AeroScience-Beta-v0.7.1-beta.2'))
    foreach ($key in 'HKCU:\Software\AeroScience','HKCU:\Software\AeroScience\Validation\v0.7.0','HKCU:\Software\AeroScience\Beta\v0.7.0-beta.1','HKCU:\Software\AeroScience\Beta\v0.7.1-beta.2') {
        $settings = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        foreach ($name in 'InstallDirectory','MissionDirectory','ScienceLibraryDirectory') { if ($settings -and $settings.$name) { $protectedRoots += [string]$settings.$name } }
    }
    foreach ($protected in $protectedRoots) {
        $boundary = [IO.Path]::GetFullPath($protected).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $candidateBoundary = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($candidateBoundary.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or $boundary.StartsWith($candidateBoundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Build outside existing application/data directories. Nothing was replaced.' }
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    if ($PSCmdlet.ParameterSetName -eq 'Build') {
        if (-not $RuntimeRedistributionConfirmed) { throw 'Use the existing specific runtime authorization with -RuntimeRedistributionConfirmed.' }
        $validationOutput = Join-Path $output 'validation'
        & "$PSScriptRoot/Build-PrivateValidation.ps1" -SdkRoot $SdkRoot -RuntimeApprovalRecord $RuntimeApprovalRecord -RuntimeRedistributionConfirmed -OutputDirectory $validationOutput @profileArguments
        $ValidationFolder = Join-Path $validationOutput $(if ($PrivateBetaVersion) { "AeroScience-$PrivateBetaVersion" } else { 'AeroScience-0.7.0-Validation' })
        $RuntimeReport = Join-Path $validationOutput 'runtime-check.json'
    }
    $destination = Join-Path $output 'beta-installer'
    $payload = Join-Path $destination 'payload'
    python scripts/release/beta_installer.py stage --folder $ValidationFolder --source $ExpectedSourceCommit --runtime-report $RuntimeReport --destination $payload @privateArguments
    if ($LASTEXITCODE -ne 0) { throw 'Beta payload verification failed. No installer was built.' }
    $dependencies = Join-Path $output 'installer-prerequisites'
    [IO.Directory]::CreateDirectory($dependencies) | Out-Null
    if (-not $VcRuntimePath) {
        $VcRuntimePath = Join-Path $dependencies 'VC_redist.x64.exe'
        Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile "$VcRuntimePath.partial" -TimeoutSec 180
        Move-Item -LiteralPath "$VcRuntimePath.partial" -Destination $VcRuntimePath
    }
    $vc = (Resolve-Path -LiteralPath $VcRuntimePath).Path
    $signature = Get-AuthenticodeSignature -LiteralPath $vc
    $vcVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($vc)
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation' -or $vcVersion.FileMajorPart -ne 14 -or $vcVersion.ProductName -notmatch 'Visual C\+\+' -or ($vcVersion.OriginalFilename -notmatch 'x64' -and $vcVersion.ProductName -notmatch 'x64')) { throw 'The bundled VC v14 x64 prerequisite must have a valid Microsoft signature.' }
    $toolReport = Join-Path $dependencies 'tools.json'
    @{ compiler_sha256=(Get-FileHash $compiler -Algorithm SHA256).Hash.ToLowerInvariant(); compiler_version=$compilerVersion.ToString(); vc_sha256=(Get-FileHash $vc -Algorithm SHA256).Hash.ToLowerInvariant(); vc_version=$vcVersion.FileVersion; vc_signature=$signature.Status.ToString(); vc_publisher=$signature.SignerCertificate.Subject; vc_architecture='x64' } | ConvertTo-Json | Set-Content -LiteralPath $toolReport -Encoding utf8
    $compilerLog = Join-Path $dependencies 'inno-build.log'
    $installerArguments = @("/DPayloadDir=$payload","/DVcRuntime=$vc","/DOutputDirectory=$destination")
    if ($PrivateBetaVersion) { $installerArguments += "/DBetaVersion=$PrivateBetaVersion" }
    & $compiler @installerArguments "$repo/scripts/installer/AeroScienceBeta.iss" *> $compilerLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $compilerLog -Tail 15; throw "Beta installer compilation failed. Preserve $compilerLog." }
    if ($PrivateBetaVersion) {
        $setup = Join-Path $destination "AeroScience-Setup-v$PrivateBetaVersion.exe"
        $setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
        $setupMetadata = ConvertFrom-InnoVersionInfo -VersionInfo $setupInfo
        $numeric = $setupMetadata.numeric_version
        if ($setupMetadata.product_version -ne $PrivateBetaVersion -or $setupMetadata.product_name -ne $identity.title -or $numeric -ne '0.7.1.2') { throw "Compiled beta installer metadata differs: product='$($setupMetadata.raw_product_name)', version='$($setupMetadata.raw_product_version)', numeric='$numeric'. No acceptance receipt was created." }
        $tools = Get-Content -LiteralPath $toolReport -Raw | ConvertFrom-Json -AsHashtable
        $tools.setup_product_version=$setupMetadata.product_version; $tools.setup_product_name=$setupMetadata.product_name
        $tools.setup_raw_product_version=$setupMetadata.raw_product_version; $tools.setup_raw_product_name=$setupMetadata.raw_product_name
        $tools.setup_numeric_version=$numeric; $tools.setup_sha256=(Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        $tools | ConvertTo-Json | Set-Content -LiteralPath $toolReport -Encoding utf8
    }
    python scripts/release/beta_installer.py record --folder $destination --source $ExpectedSourceCommit --compiler $compiler --prerequisite $vc --tool-report $toolReport @privateArguments
    if ($LASTEXITCODE -ne 0) { throw 'Beta installer receipt/checksum verification failed.' }
    Write-Host "$($identity.title) installer: $destination\AeroScience-Setup-v$($identity.version).exe"
    Write-Host "Checksum: $destination\AeroScience-Setup-v$($identity.version).exe.sha256"
    Write-Host "Source: $ExpectedSourceCommit"
    Write-Host 'Private tester beta built. Installed Windows/MSFS acceptance remains unverified. Nothing was installed or published.'
} finally { Pop-Location }
