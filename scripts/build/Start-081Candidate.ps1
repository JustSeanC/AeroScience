#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Folder,[Parameter(Mandatory)][string]$ExpectedSourceCommit)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'This explicit candidate launcher requires Windows.'}
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
& python (Join-Path $PSScriptRoot 'candidate_081.py') verify --folder $Folder --source $ExpectedSourceCommit
if($LASTEXITCODE -ne 0){throw 'Candidate identity/bytes refused. No application or original data was opened.'}
$receipt=Get-Content -LiteralPath (Join-Path $Folder 'CANDIDATE-081.json') -Raw|ConvertFrom-Json
$root=Join-Path $env:LOCALAPPDATA ('AeroScience-Candidate-v0.8.1/'+$receipt.instance)
# Validate every ancestor before creating the one compiled profile. Never follow a
# junction into an accepted installation or adopt another profile's settings.
$path=[IO.DirectoryInfo]::new($root)
while($null -ne $path){if($path.Exists -and ($path.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Candidate profile ancestor is a link/junction.'};$path=$path.Parent}
$marker=Join-Path $root 'candidate-profile.json'
if(Test-Path -LiteralPath $root){
    if(-not(Test-Path -LiteralPath $marker)){throw 'An unverified existing profile was preserved. Choose a fresh candidate attempt.'}
    if((Get-Item -LiteralPath $marker).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Candidate marker is a link.'}
    $old=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
    if($old.version -ne 1 -or $old.source_commit -ne $ExpectedSourceCommit -or $old.instance -ne $receipt.instance){throw 'Existing profile source/attempt differs. It was not opened or migrated.'}
}else{
    [IO.Directory]::CreateDirectory($root)|Out-Null
    @{version=1;source_commit=$ExpectedSourceCommit;instance=$receipt.instance}|ConvertTo-Json|Set-Content -LiteralPath $marker -Encoding utf8
}
Write-Host "Starting the isolated 0.8.1 candidate. Data/logs stay under $root. Existing installations are preserved. Close it before returning to Beta 1."
Start-Process -FilePath (Join-Path $Folder 'application/AeroScience.exe') -WorkingDirectory (Join-Path $Folder 'application')
