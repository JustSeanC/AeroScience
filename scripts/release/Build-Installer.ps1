#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateFolder,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$InnoCompiler,
    [string]$VcRuntimePath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Compile the installer on Windows with Inno Setup 6.3 or newer.' }
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Push-Location $repo
try {
    $revision = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or (git status --porcelain)) { throw 'Use clean committed source for the installer.' }
    . "$PSScriptRoot/InnoSetup.ps1"
    $compiler = Resolve-InnoSetupCompiler -InnoCompiler $InnoCompiler
    Write-Host "Inno compiler: $compiler"
    $compilerVersion = Get-InnoSetupCompilerVersion -CompilerPath $compiler
    Write-Host "Inno compiler engine: $compilerVersion"
    $metaJson = python scripts/release/package_release.py metadata
    if ($LASTEXITCODE -ne 0) { throw 'Release metadata failed.' }
    $meta = $metaJson | ConvertFrom-Json
    $destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'installer-candidate'
    if (Test-Path -LiteralPath $destination) { throw 'An installer candidate already exists. Preserve it and choose a new -OutputDirectory.' }
    $dependencies = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'installer-prerequisites'
    [IO.Directory]::CreateDirectory($dependencies) | Out-Null
    if (-not $VcRuntimePath) {
        $VcRuntimePath = Join-Path $dependencies 'VC_redist.x64.exe'
        if (-not (Test-Path -LiteralPath $VcRuntimePath)) {
            $temporary = "$VcRuntimePath.partial"
            try { Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $temporary -TimeoutSec 180; Move-Item -LiteralPath $temporary -Destination $VcRuntimePath }
            finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
        }
    }
    $vc = (Resolve-Path -LiteralPath $VcRuntimePath).Path
    $signature = Get-AuthenticodeSignature -LiteralPath $vc
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'The bundled Visual C++ prerequisite must have a valid Microsoft Authenticode signature. Download it from Microsoft; signature checks cannot be bypassed.' }
    $vcVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($vc)
    $vcProduct = $vcVersion.FileMajorPart -eq 14 -and $vcVersion.ProductName -match 'Visual C\+\+'
    $vcX64 = $vcVersion.OriginalFilename -match 'x64' -or $vcVersion.ProductName -match 'x64'
    if (-not $vcProduct -or -not $vcX64) { throw 'Expected the Microsoft Visual C++ v14 x64 redistributable.' }
    $payload = Join-Path $destination 'payload'
    python scripts/release/installer_release.py stage --candidate $CandidateFolder --destination $payload --expected-commit $revision
    if ($LASTEXITCODE -ne 0) { throw 'Installer payload staging failed.' }
    $toolReport = Join-Path $dependencies 'tools.json'
    @{ compiler_sha256=(Get-FileHash $compiler -Algorithm SHA256).Hash.ToLowerInvariant(); compiler_version=$compilerVersion.ToString();
       vc_sha256=(Get-FileHash $vc -Algorithm SHA256).Hash.ToLowerInvariant(); vc_version=$vcVersion.FileVersion;
       vc_signature=$signature.Status.ToString(); vc_publisher=$signature.SignerCertificate.Subject; vc_architecture='x64' } | ConvertTo-Json | Set-Content -LiteralPath $toolReport -Encoding utf8
    $arguments = @("/DPayloadDir=$payload", "/DVcRuntime=$vc", "/DReleaseVersion=$($meta.version)", "/DPackageVersion=$($meta.package_version)", "/DOutputDirectory=$destination", "$repo/scripts/installer/AeroScience.iss")
    $compilerLog = Join-Path $dependencies 'inno-build.log'
    & $compiler @arguments *> $compilerLog
    if ($LASTEXITCODE -ne 0) {
        $compilerExitCode = $LASTEXITCODE
        Get-Content -LiteralPath $compilerLog -Tail 15 | ForEach-Object { Write-Host $_ }
        throw "Inno Setup compilation failed (exit $compilerExitCode). Full log: $compilerLog. No installer is approved."
    }
    python scripts/release/installer_release.py record --folder $destination --compiler $compiler --prerequisite $vc --tool-report $toolReport
    if ($LASTEXITCODE -ne 0) { throw 'Installer receipt validation failed.' }
    Write-Host "Installer candidate: $destination\$($meta.setup_name)"
    Write-Host 'Compiler/payload receipt saved. The one-command release pipeline finalizes using automatic technical evidence; no manual acceptance JSON is required. Windows/MSFS hardware tests remain recommended and unverified. Nothing was published.'
} finally { Pop-Location }
