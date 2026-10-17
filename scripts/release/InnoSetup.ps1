# Shared compiler discovery and engine preflight. Dot-source from release scripts.
function Resolve-InnoSetupCompiler {
    param([string]$InnoCompiler)
    if ($InnoCompiler) {
        if (-not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) { throw "Inno compiler was not found: $InnoCompiler" }
        return (Resolve-Path -LiteralPath $InnoCompiler).Path
    }
    $locations = @()
    if ($IsWindows) {
        # Inno's uninstall registration also covers custom/per-user installation paths.
        $keys = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
        $locations += @(Get-ItemProperty -Path $keys -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } |
            ForEach-Object { Join-Path $_.InstallLocation 'ISCC.exe' })
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs' }))) {
        if ($base) { $locations += Join-Path $base 'Inno Setup 6/ISCC.exe' }
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $locations += $command.Source }
    foreach ($location in $locations) {
        if (Test-Path -LiteralPath $location -PathType Leaf) { return (Resolve-Path -LiteralPath $location).Path }
    }
    throw 'Inno compiler was not found. Install Inno Setup 6.3 or newer from https://jrsoftware.org/isdl.php, or provide -InnoCompiler with the full ISCC.exe path.'
}

function Get-InnoSetupCompilerVersion {
    param([Parameter(Mandatory)][string]$CompilerPath)
    $compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
    $directory = Join-Path ([IO.Path]::GetTempPath()) ('aeroscience-inno-probe-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    try {
        $probe = Join-Path $directory 'compiler-version.iss'
        @'
[Setup]
AppName=AeroScience compiler compatibility probe
AppVersion=0.0.0
CreateAppDir=no
Uninstallable=no
Output=no
'@ | Set-Content -LiteralPath $probe -Encoding utf8
        # ISCC.exe intentionally has 0.0.0.0 file metadata. Ask its loaded compiler
        # engine via a no-output compile; /O- works in 6.3+ (unlike newer --version).
        $PSNativeCommandUseErrorActionPreference = $false
        $output = @(& $compiler '/O-' $probe 2>&1)
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { throw "Inno compiler preflight failed (exit $exitCode) at $compiler. $($output | Select-Object -Last 1)" }
        $match = [regex]::Match(($output -join "`n"), '(?m)^Compiler engine version:\s*Inno Setup\s+(\d+\.\d+\.\d+(?:\.\d+)?)\b')
        if (-not $match.Success) { throw "Cannot identify the Inno compiler engine version at $compiler. No installer was built." }
        $version = [version]$match.Groups[1].Value
        if ($version -lt [version]'6.3.0') { throw "Inno Setup 6.3 or newer is required; compiler engine $version was detected at $compiler." }
        return $version
    }
    finally { if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force } }
}
