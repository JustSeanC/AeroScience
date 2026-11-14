#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ValidationFolder, [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedReceiptSha256,
    [string]$TestId, [ValidateSet('Passed','Failed','Blocked','Not Tested')][string]$Outcome='Not Tested',
    [string]$Observation='', [string]$EvidencePath, [switch]$Observed, [switch]$Interactive)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
& "$PSScriptRoot/Verify-080PrivateValidation.ps1" -ValidationFolder $ValidationFolder -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedReceiptSha256 $ExpectedReceiptSha256
$results=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ValidationFolder))) 'Results'
function Save-080Observation {
    $toolArguments=@("$repo/scripts/release/private_validation_080.py",'observation','--folder',$ValidationFolder,'--source',$ExpectedSourceCommit,
        '--results',$results,'--test-id',$TestId,'--outcome',$Outcome,'--notes',$Observation)
    if($Observed){$toolArguments+='--observed'}
    if($EvidencePath){$toolArguments+=@('--evidence',$EvidencePath)}
    & python @toolArguments
    if($LASTEXITCODE -ne 0){throw 'Observation refused. No previous result was overwritten.'}
}
if($Interactive){
    $template=Get-Content -LiteralPath (Join-Path $ValidationFolder 'ACCEPTANCE-TEMPLATE.json') -Raw | ConvertFrom-Json
    while($true){
        $template.tests.PSObject.Properties | ForEach-Object {Write-Host ($_.Name+': '+$_.Value.name)}
        $TestId=Read-Host 'Test ID to record (blank finishes; untested cases stay Not Tested)'
        if(-not $TestId){break}
        $Outcome=Read-Host 'Outcome: Passed / Failed / Blocked / Not Tested'
        $Observation=Read-Host 'What did you actually observe? Do not enter credentials or private correspondence'
        $Observed=(Read-Host 'Did you personally observe this test on this exact build? Type YES') -ceq 'YES'
        Save-080Observation
    }
}else{Save-080Observation}
Write-Host "Append-only owner observations: $results. A generated template is not acceptance; human translation review and public release approval remain separate."
