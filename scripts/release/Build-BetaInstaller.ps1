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
    [string]$VcRuntimePath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'Build Beta 1 on Windows x64 with PowerShell 7 and Inno Setup 6.3 or newer.' }
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    $revision = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or (git branch --show-current).Trim() -ne 'develop/v0.7.0' -or (git status --porcelain)) { throw 'Use clean committed develop/v0.7.0 source. Preserve existing work.' }
    if (-not $ExpectedSourceCommit) { $ExpectedSourceCommit = $revision }
    if ($ExpectedSourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'ExpectedSourceCommit must be the exact 40-character payload source SHA.' }
    if ($PSCmdlet.ParameterSetName -eq 'Build' -and $ExpectedSourceCommit -ne $revision) { throw 'A new build must use this checkout source SHA.' }
    if (Get-Process -Name FlightSimulator2024,fspackagetool -ErrorAction SilentlyContinue) { throw 'Close MSFS and finish any existing SDK build before packaging Beta 1.' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Preserve earlier candidates; choose a new nonexistent output directory.' }
    . "$PSScriptRoot/InnoSetup.ps1"
    $compiler = Resolve-InnoSetupCompiler -InnoCompiler $InnoCompiler
    $compilerVersion = Get-InnoSetupCompilerVersion -CompilerPath $compiler
    # Protect stable/validation data and installed applications before creating output.
    $protectedRoots = @((Join-Path $env:LOCALAPPDATA 'AeroScience'),(Join-Path $env:LOCALAPPDATA 'Programs/AeroScience'),(Join-Path $env:LOCALAPPDATA 'Programs/AeroScience-Beta-v0.7.0-beta.1'))
    foreach ($version in '0.5.0','0.6.0','0.7.0') { $protectedRoots += Join-Path $env:LOCALAPPDATA "AeroScience-Validation-v$version" }
    foreach ($key in 'HKCU:\Software\AeroScience','HKCU:\Software\AeroScience\Validation\v0.7.0','HKCU:\Software\AeroScience\Beta\v0.7.0-beta.1') {
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
        & "$PSScriptRoot/Build-PrivateValidation.ps1" -SdkRoot $SdkRoot -RuntimeApprovalRecord $RuntimeApprovalRecord -RuntimeRedistributionConfirmed -OutputDirectory $validationOutput
        $ValidationFolder = Join-Path $validationOutput 'AeroScience-0.7.0-Validation'
        $RuntimeReport = Join-Path $validationOutput 'runtime-check.json'
    }
    $destination = Join-Path $output 'beta-installer'
    $payload = Join-Path $destination 'payload'
    python scripts/release/beta_installer.py stage --folder $ValidationFolder --source $ExpectedSourceCommit --runtime-report $RuntimeReport --destination $payload
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
    & $compiler "/DPayloadDir=$payload" "/DVcRuntime=$vc" "/DOutputDirectory=$destination" "$repo/scripts/installer/AeroScienceBeta.iss" *> $compilerLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $compilerLog -Tail 15; throw "Beta installer compilation failed. Preserve $compilerLog." }
    python scripts/release/beta_installer.py record --folder $destination --source $ExpectedSourceCommit --compiler $compiler --prerequisite $vc --tool-report $toolReport
    if ($LASTEXITCODE -ne 0) { throw 'Beta installer receipt/checksum verification failed.' }
    Write-Host "Beta 1 installer: $destination\AeroScience-Setup-v0.7.0-beta.1.exe"
    Write-Host "Checksum: $destination\AeroScience-Setup-v0.7.0-beta.1.exe.sha256"
    Write-Host "Source: $ExpectedSourceCommit"
    Write-Host 'Private tester beta built. Installed Windows/MSFS acceptance remains unverified. Nothing was installed or published.'
} finally { Pop-Location }
