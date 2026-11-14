#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ValidationFolder, [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedReceiptSha256)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
& python "$repo/scripts/release/private_validation_080.py" verify --folder $ValidationFolder --source $ExpectedSourceCommit --receipt-hash $ExpectedReceiptSha256.ToLowerInvariant()
if($LASTEXITCODE -ne 0){throw 'Package verification failed. Nothing was launched or installed.'}
