#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ValidationFolder, [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedReceiptSha256)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Launch only on the owner Windows PC. Remote preparation never starts the app.'}
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
& "$PSScriptRoot/Verify-080PrivateValidation.ps1" @PSBoundParameters
# Preserve the existing one-companion exclusion contract; do not terminate another process.
$mutex=[Threading.Mutex]::new($false,'Local\AeroScience.Desktop')
try {
    try{$available=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$available=$true}
    if(-not $available){throw 'Close the existing AeroScience companion yourself before switching to private validation. Its installation/data were preserved.'}
    $mutex.ReleaseMutex()
}finally{$mutex.Dispose()}
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,5088)
try{$listener.Start()}catch{throw 'Private port 5088 is occupied. Inspect the running app yourself; no process was stopped.'}finally{$listener.Stop()}
& python "$repo/scripts/release/private_validation_080.py" prepare-profile --folder $ValidationFolder --source $ExpectedSourceCommit --local $env:LOCALAPPDATA
if($LASTEXITCODE -ne 0){throw 'The isolated profile could not be verified. Existing settings/data were not opened.'}
Write-Host 'Starting only this private companion. To activate its matching toolbar, close MSFS and explicitly choose Install in this companion Settings. Other packages are preserved.'
Start-Process -FilePath (Join-Path $ValidationFolder 'AeroScience.exe') -WorkingDirectory $ValidationFolder
