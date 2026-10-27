# Test infrastructure only. Never deploys a public server or changes an installed database/service.
#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$candidates = @(Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\initdb.exe' -ErrorAction SilentlyContinue)
if ($candidates.Count -eq 0) {
    Write-Host 'PostgreSQL binaries are unavailable on this Windows runner. PostgreSQL-specific tests report explicit skips; full Linux owns actual database/browser acceptance.'
    return
}
$pgbin = Split-Path ($candidates | Sort-Object FullName -Descending | Select-Object -First 1).FullName
$scratch = Join-Path $env:RUNNER_TEMP ('organization-pg-' + [Guid]::NewGuid().ToString('N'))
New-Item $scratch -ItemType Directory | Out-Null
$data = Join-Path $scratch 'data'
$password = Join-Path $scratch 'password'
Set-Content -LiteralPath $password -Value 'p13_disposable_loopback_only' -NoNewline
& (Join-Path $pgbin 'initdb.exe') -D $data -U aeroscience_test --auth=scram-sha-256 --pwfile=$password
if ($LASTEXITCODE -ne 0) { throw 'Isolated PostgreSQL initialization failed.' }
& (Join-Path $pgbin 'pg_ctl.exe') -D $data -l (Join-Path $scratch 'postgres.log') -o '-h 127.0.0.1 -p 55437' -w start
if ($LASTEXITCODE -ne 0) { throw 'Isolated loopback PostgreSQL startup failed.' }
try {
    $env:PGPASSWORD = 'p13_disposable_loopback_only'
    & (Join-Path $pgbin 'createdb.exe') -h 127.0.0.1 -p 55437 -U aeroscience_test aeroscience_org_tests
    if ($LASTEXITCODE -ne 0) { throw 'Isolated PostgreSQL test database creation failed.' }
} finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $password }
Add-Content -LiteralPath $env:GITHUB_ENV -Value 'AEROSCIENCE_ORG_TEST_DATABASE=Host=127.0.0.1;Port=55437;Database=aeroscience_org_tests;Username=aeroscience_test;Password=p13_disposable_loopback_only'
Write-Host 'Ready: isolated loopback PostgreSQL for Windows organization integration tests. No installed database or public service was modified.'
