#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$CommunityPackage,
      [Parameter(Mandatory)][string]$SdkReceipt,
      [Parameter(Mandatory)][string]$AcceptanceRecord,
      [switch]$RuntimeRedistributionConfirmed)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not $RuntimeRedistributionConfirmed) { throw 'Review the official SimConnect runtime redistribution terms before assembling a full release.' }
python "$PSScriptRoot/package_release.py" release --community $CommunityPackage --companion "$repo/artifacts/Windows-Companion" --sdk-receipt $SdkReceipt --acceptance $AcceptanceRecord --runtime-redistribution-confirmed --output "$repo/artifacts/AeroScience-v0.1.0.zip"
if ($LASTEXITCODE -ne 0) { throw 'Release packaging failed; no installation-ready archive was produced.' }
