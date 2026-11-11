#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeApprovalRecord,
    [switch]$RuntimeRedistributionConfirmed,
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [string]$InnoCompiler,
    [string]$VcRuntimePath,
    [string]$ExpectedSourceCommit
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Build AeroScience 0.7.1-beta.2 on Windows x64 with the genuine MSFS 2024 SDK and Inno Setup. No installer was created.'
}
# One maintained packaging pipeline. This private policy never finalizes a public release.
& "$PSScriptRoot/Build-BetaInstaller.ps1" @PSBoundParameters -PrivateBetaVersion '0.7.1-beta.2'
