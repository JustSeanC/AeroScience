# Public Windows release preparation

For 0.5.0 use the handoff below and the [current readiness report](v0.5.0-readiness.md).
The [v0.4.2 testing guide](v0.4.2-windows-testing.md) remains intact for existing testers.
Build locally with the official MSFS 2024 SDK, existing hash-bound runtime authorization
and Inno Setup; hosted Windows compilation is not an official SDK-built installer.

## Exact-source 0.5.0 candidate

First review the acceptance-correction PR and its **full Linux/Windows CI** for the chosen
commit. A branch name is not an immutable approval. Confirm the displayed SHA matches the
reviewed evidence-complete revision before building; do not use the earlier `ba96a67`
code-only commit without its later factual acceptance records.

Open **x64 Native Tools Command Prompt for Visual Studio**, then `pwsh -NoProfile` so
`cl.exe` remains on PATH. Use PowerShell 7 x64, pinned .NET SDK (`global.json`), Node/npm,
Python 3.11+, installed official SDK and Inno >=6.3. Keep existing runtime permission
correspondence private; only the matching version-2 local attestation is required.

```powershell
$ErrorActionPreference = 'Stop'
$repo = 'C:\Dev\AeroScience-release'
git -C $repo fetch origin 'refs/heads/release/v0.5.0-acceptance:refs/remotes/origin/release/v0.5.0-acceptance'
if ($LASTEXITCODE -ne 0) { throw 'Fetch failed.' }
$source = (git -C $repo rev-parse refs/remotes/origin/release/v0.5.0-acceptance).Trim()
Write-Host "Review this exact source against the PR and full CI: $source"
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$checkout = "C:\Dev\AeroScience-050-RC-$stamp"
$build = "C:\AeroScience-Builds\Science050-RC-$stamp"
git -C $repo worktree add --detach $checkout $source
if ($LASTEXITCODE -ne 0) { throw 'New exact-source checkout failed; preserve existing folders.' }
Set-Location -LiteralPath $checkout
if ((git rev-parse HEAD).Trim() -ne $source -or (git status --porcelain)) { throw 'Exact clean source required.' }
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'Windows x64 required.' }
foreach ($tool in 'git','dotnet','node','npm','python','cl.exe') { Get-Command $tool -ErrorAction Stop | Select-Object Name,Source }
$sdk = 'C:\MSFS 2024 SDK'
$runtime = Join-Path $sdk 'SimConnect SDK\lib\SimConnect.dll'
$approval = 'C:\AeroScience-Release-Records\runtime-approval.json'
$inno = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
foreach ($path in @($runtime,$approval,$inno,(Join-Path $sdk 'Tools\bin\fspackagetool.exe'),(Join-Path $sdk 'SimConnect SDK\include\SimConnect.h'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required local file missing: $path" }
}
$review = Get-Content -LiteralPath $approval -Raw | ConvertFrom-Json
$runtimeHash = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash.ToLowerInvariant()
if ($review.format_version -ne 2 -or $review.redistribution_permitted -ne $true -or $review.conditions_reviewed -ne $true -or $review.runtime_sha256 -ne $runtimeHash) { throw 'Runtime attestation does not match the actual DLL; follow docs/runtime-distribution.md.' }
Get-PSDrive -PSProvider FileSystem | Select-Object Name,Used,Free
python scripts/release/verify_scientific_acceptance.py
if ($LASTEXITCODE -ne 0) { throw 'Scientific gate failed; do not bypass or restamp evidence.' }
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\Build-Release.ps1 `
    -SdkRoot $sdk -RuntimePath $runtime -RuntimeApprovalRecord $approval `
    -RuntimeRedistributionConfirmed -InnoCompiler $inno -OutputDirectory $build
if ($LASTEXITCODE -ne 0) { throw 'Build failed; preserve all receipts and artifacts.' }
Get-FileHash -LiteralPath "$build\installer-candidate\AeroScience-Setup-v0.5.0.exe" -Algorithm SHA256
```

Use the real installed paths if different. Confirm adequate source/output/temp disk space;
the builder runs locked restore, .NET/browser/helpers/recovery, scientific verification,
actual SDK ABI/Community toolbar compilation, native runtime loading/exports, packaging
and Inno checks. It never replaces an existing candidate. Review any failure before
trying a new output folder. No alternative DLL or structural CI fixture is a substitute.

Expected outputs in `$build` (not produced in cloud):

- `installer-candidate\AeroScience-Setup-v0.5.0.exe`, its `.sha256`, `build-receipt.json`
  and `ACCEPTANCE.json`.
- `build-evidence\` with TRX/browser/helpers/recovery/technical-validation receipts,
  actual runtime report and installer prerequisite/compiler receipts.
- `AeroScience-v0.5.0-win-x64-CANDIDATE.zip` and matching unpacked candidate folder.

## Installed owner acceptance

Follow [workbook A–K](v0.5.0-acceptance-workbook.md): simulator/telemetry, genuine toolbar,
planning/preparation/readiness, multi-session controls, live operating status, in-flight
priority, archived science/replay/exports, storage/recovery, genuine external data,
physical iPad/Quest and a 30–60-minute mission. Group J independently requires authorized
Classic and NG originals plus numerical comparison; Cuprite and CMR cannot replace it.
Use disposable/copied 0.4.2 data for upgrade/uninstall tests and preserve the public release.

The private guide/workbook's isolated Validation profile and production candidate differ:
the official installer uses production identities/port 5080. Back up production data before
testing its upgrade path. Validate the actual installed payload and record actual results:

```powershell
python scripts/release/installer_release.py verify-installed --folder "$build\installer-candidate" --installed "$env:LOCALAPPDATA\Programs\AeroScience"
if ($LASTEXITCODE -ne 0) { throw 'Installed files differ from the candidate.' }
pwsh -NoProfile -STA -ExecutionPolicy RemoteSigned -File .\scripts\release\Complete-ReleaseAcceptance.ps1 -AcceptanceRecord "$build\installer-candidate\ACCEPTANCE.json"
```

Preserve source, EXE/checksum, SDK/runtime and compiler identities; hash the EXE before and
after testing. Do not rebuild between testing and later finalization. Unexecuted tests stay
Not Tested; the graphical checklist does not replace the 0.5.0 workbook or NASA evidence.

Stop at candidate testing/recording for this task. Finalization with `New-Release.ps1
-AcceptanceRecord` and publication are separate owner decisions. `Publish-Release.ps1`
creates tags/releases and is **not** a preview. No 0.5.0 tag, installer upload or public
release is authorized here. Draft notes are maintained in [CHANGELOG](../CHANGELOG.md).

Release notes include the optional [official Discord community](https://discord.gg/mpYyazCqvJ) alongside existing feedback and GitHub support. No account or membership is required to use the application.
