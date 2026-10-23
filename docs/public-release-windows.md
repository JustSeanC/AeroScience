# Public Windows release preparation

For stable 0.5.0 use the handoff below and the [current readiness report](v0.5.0-readiness.md).
The [v0.4.2 testing guide](v0.4.2-windows-testing.md) remains intact for existing testers.
Build locally with the official MSFS 2024 SDK, existing hash-bound runtime authorization
and Inno Setup; hosted Windows compilation is not an official SDK-built installer.

## Exact-source 0.5.0 candidate

Use the final frozen main SHA from the stable-release report and its **full Linux/Windows CI**.
A moving branch name is not an immutable approval. PR #17 scientific corrections and PR #18
Planner corrections must both be included, with stable release metadata and fresh scientific
records. A prerelease/private validation candidate cannot substitute for that stable source.

Open **x64 Native Tools Command Prompt for Visual Studio**, then `pwsh -NoProfile` so
`cl.exe` remains on PATH. Use PowerShell 7 x64, pinned .NET SDK (`global.json`), Node/npm,
Python 3.11+, installed official SDK and Inno >=6.3. Keep existing runtime permission
correspondence private; only the matching version-2 local attestation is required.

```powershell
$ErrorActionPreference = 'Stop'
$repo = 'C:\Dev\AeroScience-release'
git -C $repo fetch origin 'refs/heads/main:refs/remotes/origin/main'
if ($LASTEXITCODE -ne 0) { throw 'Fetch failed.' }
$source = '<paste the exact frozen main SHA from the stable-release report>'
if ($source -notmatch '^[0-9a-f]{40}$') { throw 'Use the reported immutable 40-character source SHA.' }
git -C $repo merge-base --is-ancestor $source refs/remotes/origin/main
if ($LASTEXITCODE -ne 0) { throw 'Frozen source is not part of remote main.' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$checkout = "C:\Dev\AeroScience-050-Stable-$stamp"
$build = "C:\AeroScience-Builds\Science050-Stable-$stamp"
git -C $repo worktree add --detach $checkout $source
if ($LASTEXITCODE -ne 0) { throw 'New exact-source checkout failed; preserve existing folders.' }
Set-Location -LiteralPath $checkout
if ((git rev-parse HEAD).Trim() -ne $source -or (git status --porcelain)) { throw 'Exact clean source required.' }
$meta = (python scripts/release/package_release.py metadata) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $meta.version -ne '0.5.0' -or $meta.prerelease -ne $false -or $meta.title -ne 'AeroScience v0.5.0 — Scientific Mission Operations') { throw 'Frozen stable identity is required.' }
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

## Preserve owner approval and verify the final candidate

The owner reports successful local pre-PR #18 0.5.0 Windows/MSFS testing, approves that tested
version, and explicitly accepts shipping the reviewed changes without another functional build test.
That approval is accepted, not replaced by cloud tests. Retain its actual ACCEPTANCE.json, source
SHA, installer/payload hashes, build receipts and notes. The cloud has no identified matching
0.5.0 installer: older uploaded 0.2.1 records are legitimate history, not 0.5.0 acceptance.

Do not repeat unrelated completed tests merely because the release stage changed. Identify the
previous build and review its diff against the frozen stable source. The owner's approval covers the
reviewed PR #18 correction without another Windows/MSFS functional test. Keep applicable actual results
with references to their original evidence and explain why they remain applicable. Specifically
retain the automated corrected Planner/payload and stable update-preference regressions, verify
installed payload identity and preserve any actual changed-installation evidence. B04/B05 physical
testing remains unperformed unless separately recorded; owner acceptance of that delta is not a
manufactured test pass.
If bytes/source differ, do not copy the previous installer hash or assert identical-byte testing.
The final candidate's acceptance record must bind its actual source, installer/payload and receipt.
Only the owner can supply missing actual test evidence; no bulk automatic pass is appropriate.

Follow [workbook A–K](v0.5.0-acceptance-workbook.md): simulator/telemetry, genuine toolbar,
planning/preparation/readiness, multi-session controls, live operating status, in-flight
priority, archived science/replay/exports, storage/recovery, genuine external data,
physical iPad/Quest and a 30–60-minute mission, using existing applicable recorded evidence where
available. Group J independently tracks authorized Classic and NG originals plus numerical
comparison; Cuprite and CMR cannot replace it or fill unperformed checks.
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

## Finalize and publish the accepted stable bytes

The owner explicitly authorizes stable publication. This authorization does not bypass a missing
official candidate or any existing scientific/build/exact-installer acceptance gate. Once those
checks pass, use the same source checkout and candidate without rebuilding:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\New-Release.ps1 `
    -AcceptanceRecord "$build\installer-candidate\ACCEPTANCE.json" -OutputDirectory $build
if ($LASTEXITCODE -ne 0) { throw 'Finalization refused; inspect the actual gate failure.' }
$setup = "$build\AeroScience-Setup-v0.5.0.exe"
$archive = "$build\AeroScience-v0.5.0-win-x64.zip"
$record = "$build\AeroScience-Setup-v0.5.0.release.json"
python scripts/release/installer_release.py verify --archive $archive --setup $setup --record $record --expected-commit $source
if ($LASTEXITCODE -ne 0) { throw 'Exact finalized artifacts failed verification.' }
Get-FileHash -LiteralPath $setup -Algorithm SHA256
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\Publish-Release.ps1 `
    -Archive $archive -Setup $setup -IncludePortableArchive
if ($LASTEXITCODE -ne 0) { throw 'Publication/ordinary public-download verification failed; preserve artifacts and logs.' }
gh api repos/JustSeanC/AeroScience/releases/tags/v0.5.0 --jq '{tag_name,name,draft,prerelease,html_url,assets:[.assets[]|{name,size,digest,browser_download_url}]}'
```

The publisher validates the source-bound finalized installer, creates `v0.5.0` only at that source,
uploads EXE/checksum/sanitized evidence (and optional verified portable ZIP), then publishes with
`draft=false` and `prerelease=false`. It re-downloads public assets without authentication and
compares actual hashes. Never create an empty tag/release to compensate for an unavailable build.
Keep the published 0.4.2 release/assets/tag unchanged.

After successful publication and source/asset verification, forward-merge that exact frozen source
into existing `develop/v0.6.0`, preserving every development commit and its development identity.
Do not perform this step before publication or copy stable 0.5.0 identity over 0.6.0 configuration.
Do not recreate/reset that branch. Current scope/readiness and limitations remain in
[CHANGELOG](../CHANGELOG.md) and the [readiness report](v0.5.0-readiness.md).

Release notes include the optional [official Discord community](https://discord.gg/mpYyazCqvJ) alongside existing feedback and GitHub support. No account or membership is required to use the application.
