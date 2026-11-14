#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [string]$RuntimeApprovalRecord = 'C:\AeroScience-Release-Records\runtime-approval.json',
    [switch]$RuntimeRedistributionConfirmed,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Install Python 3 and make python available on PATH. Nothing was built or installed.' }
$toolArguments = @("$repo/scripts/release/private_validation_080.py", 'preflight', '--source', $ExpectedSourceCommit,
    '--sdk', $SdkRoot, '--approval', $RuntimeApprovalRecord, '--output', $OutputDirectory)
if ($RuntimeRedistributionConfirmed) { $toolArguments += '--confirmed' }
if ($ReportPath) { $toolArguments += @('--report', $ReportPath) }
$PSNativeCommandUseErrorActionPreference = $false
& python @toolArguments
if ($LASTEXITCODE -ne 0) { throw 'Native preflight is blocked. Follow the named next_action items above; no Windows/MSFS acceptance is claimed.' }
