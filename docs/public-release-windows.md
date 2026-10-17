# Build, test and publish the Windows installer

The primary download is **AeroScience-Setup-v0.1.1-alpha.exe**. A self-hosted GitHub Actions runner is not required. These are maintainer instructions; testers run the installer and use Start → AeroScience without developer commands.

The maintainer already confirmed native SimConnect redistribution authorization with no additional conditions. Keep the correspondence private. Reuse `C:\AeroScience-Release-Records\runtime-approval.json` while it matches the actual SDK DLL. No new authorization request is needed.

## 1. Install the additional build tool and update source

Install **Inno Setup 6.3 or newer** from [its official download page](https://jrsoftware.org/isdl.php), reviewing the framework license. A standard Inno Setup 6 installation provides `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`. A custom/newer installation can be supplied with `-InnoCompiler 'full path to ISCC.exe'`.

Keep the already installed .NET SDK pinned by `global.json`, Node 22+, Python 3.10+, Git, PowerShell 7, Visual Studio C++ x64 build tools and official MSFS 2024 SDK. The user has built the earlier ZIP candidate successfully; the changed app/wizard/installer requires a new candidate and acceptance. Preserve that earlier candidate and all original mission data.

Open **Start → x64 Native Tools Command Prompt for Visual Studio**, then:

```powershell
pwsh -NoProfile
Set-Location 'C:\Dev\AeroScience-release'
git pull --ff-only origin main
git rev-parse HEAD
```

Record the printed 40-character commit. Build from clean committed source. Close the old companion before testing an upgrade and retain a separate backup of its data while it is stopped.

If a new checkout is needed, use `git clone https://github.com/JustSeanC/AeroScience.git C:\Dev\AeroScience-release` in an unused folder. A source ZIP has no required Git provenance. If the runtime attestation has not yet been created, [New-RuntimeApproval.ps1](runtime-distribution.md) records only the confirmed public summary/hash; do not copy correspondence into source or distribution folders.

## 2. Build a new installer candidate

Use a fresh output directory to retain the previous ZIP candidate:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\Build-ReleaseCandidate.ps1 -SdkRoot 'C:\MSFS 2024 SDK' -RuntimeApprovalRecord 'C:\AeroScience-Release-Records\runtime-approval.json' -RuntimeRedistributionConfirmed -OutputDirectory 'C:\AeroScience-Builds\InstallerAlpha1'
```

For a custom Inno location, append `-InnoCompiler 'C:\path\ISCC.exe'`. The build downloads the full Microsoft Visual C++ x64 redistributable from Microsoft's official endpoint and requires its valid Microsoft Authenticode signature and x64 product identity. If that download is unavailable, download it through Microsoft's [official page](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist), then append `-VcRuntimePath 'C:\path\VC_redist.x64.exe'`; signature/identity checks remain mandatory.

The script runs locked restore, application/helper/recovery tests, self-contained Windows publishing, installed SDK ABI checks, genuine package compilation/validation, native runtime loading/export checks and permission/hash validation. It stages a validated payload, compiles Inno Setup and writes its SHA-256, compiler/prerequisite receipt and a new acceptance template. Failed stages stop release preparation. Logs include `installer-prerequisites\inno-build.log` under the chosen output directory and the SDK logs under the source checkout's `artifacts\toolbar-full`.

The build prints the selected Inno compiler path and **engine** version before the longer build stages. `ISCC.exe` can legitimately report `0.0.0.0` for its Windows FileVersion/ProductVersion; those fields are not the Inno engine version. Preflight performs a tiny no-output compile and checks its successful engine report against the 6.3 minimum. No test installer is created or launched. Custom/per-user paths are read from Windows installed-program registration; `-InnoCompiler` remains available.

If you reached the old `FileMinorPart`/"6.3 or newer" exception despite having Inno 6.7, pull the correction and build from its new clean source revision into a fresh folder such as `C:\AeroScience-Builds\InstallerAlpha2`. Keep the earlier `InstallerAlpha1` payload. The changed source commit requires a new candidate so that application, SDK and installer evidence continue to match; do not edit out the version check or relabel the earlier payload.

**Run this candidate installer:**

```text
C:\AeroScience-Builds\InstallerAlpha1\installer-candidate\AeroScience-Setup-v0.1.1-alpha.exe
```

Beside it are `.exe.sha256`, `build-receipt.json`, `ACCEPTANCE.json` and the exact `payload` directory. The final filename is already used inside this clearly separated candidate directory, allowing publication of the identical tested EXE. Nothing is uploaded. The intermediate ZIP is an internal/optional portable payload, not the recommended download.

## 3. Verify Windows installation and onboarding

A Linux cross-publish or compiler fixture does not count as Windows acceptance. Use a disposable supported Windows test environment for fresh installation/prerequisites/uninstall and the gaming PC for real MSFS integration.

For Windows Sandbox where available (Windows Pro/Enterprise with that optional feature enabled): create `C:\AeroScience-Installer-Test` in File Explorer, copy only the candidate setup EXE there, then open [AeroScience.wsb](../scripts/installer/AeroScience.wsb). It maps that folder read-only at `C:\InstallerInput` with internet disabled. In Sandbox run the EXE graphically. Do not map your valuable original archive into the disposable environment. Sandbox is optional; another Windows VM is suitable. Neither requires a GitHub runner.

Record actual OS/app/MSFS/SDK versions and evidence for these tests:

1. **Fresh install:** Welcome/version/path/progress, notices, bundled prerequisite route on clean Windows, Start shortcut, optional desktop shortcut, Installed Apps entry and launch-at-finish. Start the app with no SDK/developer tools/server commands. Use an installation folder containing spaces too.
2. **First-run wizard:** confirm defaults and independently choose data folders (including separate real drives on the gaming PC), available capacity and the large-data notice. Check nonempty destinations are refused, original data remains and application folders cannot be selected for data. Cancel/reopen setup and verify it resumes. In a VM with no MSFS, Skip for Now must work.
3. **Toolbar:** on the gaming PC review the discovered MSFS 2024 Steam/Store configured folder, or Browse. Approve install with MSFS closed; verify the genuine toolbar opens in MSFS. Retest with MSFS running: Community must remain unchanged and the wizard must offer close/skip. Update an existing AeroScience package, confirm one active copy and preserved unrelated add-ons. An installation on Steam does not establish actual Store acceptance; record untested layouts honestly.
4. **Live flight:** load an aircraft, require its identity/fresh actual SimConnect values, record a short mission, end it and inspect samples/history. Test current native dragging/resizing/scrolling. Check runtime loading without manually copying a DLL. Mach/cadence and other aircraft remain separate extended tests.
5. **Offline archive:** close MSFS and internet, reopen from Start, inspect existing and new completed missions/details and download a complete JSON export. Restart the app and confirm modes/samples/history remain.
6. **Upgrade preservation:** retain a completed mission and a small test note in the Science Library; record both paths and mission ID/mode/count. Close the app and install the same candidate over its existing installation (and test adoption of the earlier preview's archive). Defaults must retain paths/settings/data. Explicit requested moves must use verified migration, retain originals, handle conflicts and block active recording. A same-version reinstall is initial preservation evidence, not a claim that an unbuilt future version was tested.
7. **Uninstall/reinstall:** use Installed Apps. Test keeping the toolbar and, on a second install, explicitly removing it with MSFS closed. Check app/shortcuts are removed, data/settings remain, only AeroScience leaves Community, its backup exists and other add-ons remain. With MSFS running removal must preserve Community and explain the error. Reinstall and confirm retained archive access.

For file-integrity acceptance, run this maintainer-only check after normal installation (adjust `--installed` if the application path changed):

```powershell
python .\scripts\release\installer_release.py verify-installed --folder 'C:\AeroScience-Builds\InstallerAlpha1\installer-candidate' --installed "$env:LOCALAPPDATA\Programs\AeroScience"
```

It checks every installed payload file against the compiler receipt, including the native runtime and genuine toolbar. Installer-created uninstall files are expected additional files. The setup log is `%TEMP%\Setup Log ... .txt`; review before sharing. AeroScience's Collect Feedback provides application logs without bundling databases/SDK DLLs by default.

## 4. Complete installer acceptance and finalize without rebuilding

Copy `installer-candidate\ACCEPTANCE.json` to `C:\AeroScience-Release-Records\acceptance-installer-v0.1.1-alpha.json`. Edit the copy in Notepad. Keep its generated `version`, `source_commit`, `payload_sha256`, `installer_sha256` and `installer_receipt_sha256` unchanged. Enter a tester label, actual ISO timestamp with timezone and real evidence for each required check. The eleven minimum checks now include installer installation, first-run wizard, upgrade preservation and uninstall preservation alongside the existing app/archive/SDK-runtime/live checks. Mark only actual passes as passed. Failed/blocked/pending required checks stop finalization.

Extended recovery/migration edge cases, Mach/cadence/aircraft variety, iPad and VR may remain explicitly pending for this alpha; stable gates retain extended acceptance. If a required installer behavior fails, fix it, build a new candidate in another output directory and retest. Never transplant the old ZIP's acceptance or change hashes to pass.

After genuine acceptance:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\New-Release.ps1 -AcceptanceRecord 'C:\AeroScience-Release-Records\acceptance-installer-v0.1.1-alpha.json' -OutputDirectory 'C:\AeroScience-Builds\InstallerAlpha1'
```

This revalidates the exact EXE/receipt/payload/acceptance and copies the tested EXE without recompilation or alteration. Final application acceptance is external to the installed EXE, avoiding an EXE rebuild just to change its review record. The internal portable archive receives that same final record and is CRC/hash/provenance validated.

**Final primary release files:**

```text
C:\AeroScience-Builds\InstallerAlpha1\AeroScience-Setup-v0.1.1-alpha.exe
C:\AeroScience-Builds\InstallerAlpha1\AeroScience-Setup-v0.1.1-alpha.exe.sha256
C:\AeroScience-Builds\InstallerAlpha1\AeroScience-Setup-v0.1.1-alpha.release.json
C:\AeroScience-Builds\InstallerAlpha1\RELEASE-NOTES.md
```

The EXE, checksum and public release-evidence JSON are the recommended uploads; paste the Markdown as the release description. The optional secondary `AeroScience-v0.1.1-alpha-win-x64.zip` and its checksum are in the same directory. Keep that internal archive locally even when not uploading it: final verification and optional automated publishing use it to recheck genuine payload/SDK/runtime evidence. Review acceptance text for private information before publication. Original correspondence never belongs in these files.

Without `-OutputDirectory`, build/finalization default to `C:\Dev\AeroScience-release\artifacts`. Existing candidates/final assets are not overwritten.

## 5. Publish through GitHub's website

Create the source tag at the exact tested `source_commit` from the acceptance record (replace the placeholder):

```powershell
git tag v0.1.1-alpha TESTED_40_CHARACTER_COMMIT
git push origin refs/tags/v0.1.1-alpha
```

If a tag already exists, stop and verify it; do not force-move it.

1. Visit [AeroScience Releases](https://github.com/JustSeanC/AeroScience/releases), sign in and choose **Draft a new release**.
2. Select the existing **v0.1.1-alpha** tag. Title: **AeroScience v0.1.1 — Live Flight Recording Alpha**.
3. Paste the generated `RELEASE-NOTES.md`. Its first download link recommends the setup EXE and identifies the tested commit/checksum and remaining limitations.
4. Attach the final **setup EXE**, **EXE.sha256**, and **.release.json** listed above. Optional: add the portable ZIP/checksum as secondary assets. Do not upload the pending candidate directory, private records or correspondence.
5. Check **Set as a pre-release**, review the source tag/assets, then **Publish release**.
6. In a signed-out browser download the setup and sidecar, verify the hash and run that download in your Windows test environment. Confirm ordinary public access before sharing.

Expected URLs only after actual publication:

- https://github.com/JustSeanC/AeroScience/releases/tag/v0.1.1-alpha
- https://github.com/JustSeanC/AeroScience/releases/download/v0.1.1-alpha/AeroScience-Setup-v0.1.1-alpha.exe
- https://github.com/JustSeanC/AeroScience/releases/download/v0.1.1-alpha/AeroScience-Setup-v0.1.1-alpha.exe.sha256

No new installer EXE, final installer hash, successful Windows installation/uninstall or public asset is claimed by this document. Project installer signing is not configured; do not describe this alpha as signed. A future signing step must run before generating the tested EXE receipt.

## Optional future automation

The manual Actions workflow can prepare installer candidates on a trusted Windows SDK/Inno machine and later finalize/publish that same tested candidate on a GitHub-hosted Windows runner. It uploads the setup EXE/checksum/public evidence by default; portable assets require `-IncludePortableArchive`. Only publication receives `contents: write`. No self-hosted runner or special release token is required for the manual local/web process above. Public push/PR CI may compile the installer recipe with explicit structural fixtures, never publish those fixtures or call them simulator acceptance.
