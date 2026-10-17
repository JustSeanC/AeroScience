# Build, test and publish the v0.2.2 Windows installer

**Primary asset: AeroScience-Setup-v0.2.2.exe.** This guide is for the maintainer; testers run Setup and use Start → AeroScience. No self-hosted Actions runner is required. The new source has not yet produced a Windows/official-SDK installer in the cloud.

## Update and build on the gaming PC

Keep the installed .NET SDK pinned by `global.json`, Node 22+, Python 3.10+, Git, PowerShell 7, Visual Studio C++ x64 build tools, official MSFS 2024 SDK and Inno Setup 6.3+ (your 6.7.3 is supported). Keep the existing v0.2.1 installer and stopped-data backups. Close MSFS and AeroScience before the upgrade test. Do not delete existing stores/settings.

Open **Start → x64 Native Tools Command Prompt for Visual Studio**, then enter:

```powershell
pwsh -NoProfile
Set-Location 'C:\Dev\AeroScience-release'
git pull --ff-only origin main
git status --short
git rev-parse HEAD
```

Build from a clean committed checkout; record its printed 40-character commit. If the folder does not exist, clone into an unused folder with `git clone https://github.com/JustSeanC/AeroScience.git C:\Dev\AeroScience-release` first. A source ZIP lacks required Git provenance.

The existing confirmed runtime redistribution authorization has **no additional conditions**. Reuse `C:\AeroScience-Release-Records\runtime-approval.json` while its hash matches the native SDK DLL. Keep original correspondence private. [Runtime documentation](runtime-distribution.md) explains creating a new hash-bound public attestation if the actual DLL changes; no original correspondence belongs in source or release assets.

Use a fresh build destination:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\Build-ReleaseCandidate.ps1 -SdkRoot 'C:\MSFS 2024 SDK' -RuntimeApprovalRecord 'C:\AeroScience-Release-Records\runtime-approval.json' -RuntimeRedistributionConfirmed -InnoCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' -OutputDirectory 'C:\AeroScience-Builds\Geographic022-1'
```

This runs locked restore/.NET tests, JavaScript checks, helper tests and crash recovery, publishes the self-contained app, compiles the native ABI checks against your actual SDK, SDK-builds the full Community package, validates its genuine receipt, loads/checks the licensed x64 runtime (now also `SimConnect_GetLastSentPacketID`), and compiles Inno Setup with the signed Microsoft VC++ prerequisite. Compiler logs are retained. An SDK/provenance/runtime/compile failure stops release preparation; do not bypass it. The internal candidate ZIP is not the recommended tester download.

Expected **candidate**, only after success:

```text
C:\AeroScience-Builds\Geographic022-1\installer-candidate\AeroScience-Setup-v0.2.2.exe
C:\AeroScience-Builds\Geographic022-1\installer-candidate\ACCEPTANCE.json
```

## Test the exact candidate

Run that setup EXE. Use the [v0.2.2 Windows test sequence](v0.2.2-windows-testing.md). A clean Windows account/environment checks dependencies without developer tools; a backed-up v0.2.1 profile checks upgrade preservation. The actual gaming PC is required for new moving-map/EFB/geographic and MSFS toolbar results. Browser mocks/cross-publish are not those results.

The generated acceptance record identifies the EXE, source and payload. Record actual tester/date (`2026-10-17T14:54:00Z` is an example format, not a claimed test time) and evidence only for checks you performed. Existing eleven installer/live/archive minimum checks remain, with five additional grouped checks: `science_operations`, `research_archive`, `data_management`, `toolbar_update_progress`, `mission_experience`. The record's instructions explain each. Pending extended aircraft/storage/recovery/Mach/VR/iPad checks remain disclosed in prerelease notes. Required failures/pending checks stop finalization. Never copy old v0.2.1 acceptance to this changed EXE.

Optionally verify the installed files:

```powershell
python .\scripts\release\installer_release.py verify-installed --folder 'C:\AeroScience-Builds\Geographic022-1\installer-candidate' --installed "$env:LOCALAPPDATA\Programs\AeroScience"
```

## Finalize the tested bytes

Once the exact candidate's required checks actually pass:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\New-Release.ps1 -AcceptanceRecord 'C:\AeroScience-Builds\Geographic022-1\installer-candidate\ACCEPTANCE.json' -OutputDirectory 'C:\AeroScience-Builds\Geographic022-1'
```

This validates and copies the **same tested EXE**, without rebuilding. Expected final files:

```text
C:\AeroScience-Builds\Geographic022-1\AeroScience-Setup-v0.2.2.exe
C:\AeroScience-Builds\Geographic022-1\AeroScience-Setup-v0.2.2.exe.sha256
C:\AeroScience-Builds\Geographic022-1\AeroScience-Setup-v0.2.2.release.json
C:\AeroScience-Builds\Geographic022-1\RELEASE-NOTES.md
```

No final checksum or EXE exists merely because these paths are documented. Keep the optional/internal ZIP and evidence locally for re-verification. Do not edit/re-sign/rebuild the accepted EXE afterward; changes require testing the changed bytes. The project EXE currently has no configured Authenticode certificate; do not promise an unsigned installer is recognized by Windows. Microsoft prerequisite signatures are separately checked.

## Publish manually through GitHub

1. Confirm the finalizer's source commit and SHA-256. Back up the source commit to `main`. Create tag **v0.2.2** at that exact tested commit, not a later HEAD. From the source checkout, run `git tag v0.2.2 TESTED_40_CHARACTER_COMMIT`, then `git push origin refs/tags/v0.2.2`. If the tag already exists at another commit, stop and investigate rather than replacing it.
2. Open [Create release](https://github.com/JustSeanC/AeroScience/releases/new), select **v0.2.2**, title **AeroScience v0.2.2 — Geographic Mission Integration Prerelease**.
3. Paste the finalized `RELEASE-NOTES.md`. Keep actual tested scope and pending device/custom-aircraft limitations visible.
4. Upload the final **AeroScience-Setup-v0.2.2.exe**, matching `.exe.sha256` and `.release.json`. The setup EXE is the primary download; the portable ZIP may be an optional secondary asset.
5. Check **Set as a pre-release**. `ReleaseChannel=prerelease` preserves this policy even though the version/filename has no `-alpha` suffix. Review private paths/free-text evidence before publishing; correspondence stays private.
6. Publish, then open the release page and test the setup EXE download and checksum from a normal browser. GitHub “Source code” downloads are not the application.

Intended URL after actual publication: `https://github.com/JustSeanC/AeroScience/releases/tag/v0.2.2`. Do not describe it as a working binary download before assets are uploaded and checked. The optional Actions workflow remains available for future automation and is not part of this manual path.
