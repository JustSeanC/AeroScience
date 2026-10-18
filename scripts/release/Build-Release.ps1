#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [string]$RuntimePath,
    [Parameter(Mandatory)][string]$RuntimeApprovalRecord,
    [switch]$RuntimeRedistributionConfirmed,
    [string]$OutputDirectory,
    [string]$InnoCompiler,
    [string]$VcRuntimePath
)
# One Windows command; all technical checks remain in the shared pipeline.
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Build-ReleaseCandidate.ps1" @PSBoundParameters
