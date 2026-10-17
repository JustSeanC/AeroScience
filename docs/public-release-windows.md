# First public alpha: build on the gaming PC, upload through the website

No self-hosted GitHub Actions runner is required. You are the maintainer for the following steps; ordinary testers receive only the final ZIP and use its graphical app. Nothing below bypasses the official SDK, specific runtime permission or actual Windows/MSFS acceptance.

**Current status:** the maintainer confirms native SimConnect redistribution authorization, with no additional conditions reported. Original correspondence stays private. The exact DLL hash/loading, new official SDK build and minimum Windows/MSFS checks still need to be completed on the gaming PC. No complete public release asset or final checksum has been produced here.

## 1. Use a fresh Git checkout and the existing Windows tools

Keep `C:\Windows-Companion-unverified-in-MSFS` and your mission data. Do not overwrite the old preview or delete `%LOCALAPPDATA%\AeroScience`. The new app makes verified pre-upgrade database backups; retain a separate personal backup before testing an upgrade.

The maintainer PC needs Git for Windows, PowerShell 7, the .NET SDK pinned in `global.json`, Node 22+, Python 3, Visual Studio C++ x64 build tools and your official SDK at `C:\MSFS 2024 SDK`. The existing toolbar build already used several of these. Install missing maintainer tools from their official sites. Testers need none of them.

Open **Start → x64 Native Tools Command Prompt for Visual Studio** (the installed version's name may vary). Type this to enter PowerShell while retaining the compiler environment:

```powershell
pwsh -NoProfile
```

Then, in that PowerShell window:

```powershell
git clone https://github.com/JustSeanC/AeroScience.git C:\Dev\AeroScience-release
Set-Location C:\Dev\AeroScience-release
git rev-parse HEAD
```

Use a new folder if that one already exists. Keep the printed 40-character commit; it is the source revision being built. Source must be clean/committed. GitHub's **Code → Download ZIP** lacks release provenance and is not the tester download.

## 2. Record the confirmed authorization without sharing correspondence

The maintainer has confirmed redistribution is authorized and reports no additional conditions. From the same PowerShell window at `C:\Dev\AeroScience-release`, run:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\New-RuntimeApproval.ps1 -RedistributionAuthorized -ConditionsSummary 'No additional distribution conditions reported by the project maintainer.'
```

It hashes the actual `C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll` and creates `C:\AeroScience-Release-Records\runtime-approval.json` using a public maintainer label and current UTC record timestamp. No manual JSON editing, copied DLL or correspondence upload is needed. The helper does not claim runtime loading or SDK/hardware acceptance; those checks follow in the candidate build.

Keep the original correspondence separately and privately. Only the summary/hash/conditions attestation is packaged. If an approval record already exists, preserve it and choose another `-OutputPath`, then use that new path in the next command. See [runtime-distribution.md](runtime-distribution.md) for the exact public/private separation.

## 3. Build a complete candidate ZIP locally

From the same PowerShell window at `C:\Dev\AeroScience-release`:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\Build-ReleaseCandidate.ps1 -SdkRoot 'C:\MSFS 2024 SDK' -RuntimeApprovalRecord 'C:\AeroScience-Release-Records\runtime-approval.json' -RuntimeRedistributionConfirmed
```

The script builds/tests the companion, checks packaging helpers and crash recovery, compiles the installed SDK ABI contract, runs Microsoft's package builder/validation, validates the actual native runtime and assembles the app plus toolbar. No SDK binary is invented. A failed command stops the process; keep the reported logs rather than editing manifests/receipts to pass.

The candidate is:

```text
C:\Dev\AeroScience-release\artifacts\AeroScience-v0.1.1-alpha-win-x64-CANDIDATE.zip
C:\Dev\AeroScience-release\artifacts\AeroScience-v0.1.1-alpha-win-x64-CANDIDATE.zip.sha256
```

Its working copy is `artifacts\release-candidate\AeroScience-v0.1.1-alpha-win-x64`. Earlier candidates are never overwritten. For a new build, use another output directory with `-OutputDirectory 'C:\AeroScience-Builds\Attempt2'`; use that same directory during finalization. Do not publish a candidate ZIP.

## 4. Test the extracted candidate and record minimum acceptance

Right-click the **CANDIDATE ZIP → Extract All**, choose a new writable test folder such as `C:\AeroScience-Alpha-Test`, and keep all files together. Test the extracted copy, not the source/build directories. The ZIP contains one folder with AeroScience.exe directly inside it.

1. With MSFS closed, double-click AeroScience.exe. Confirm the app launches and the dashboard opens automatically. Verify its local mission archive, details and export are available without MSFS/internet. Check existing history is retained.
2. With MSFS closed, use Settings → General & Toolbar → Browse (if needed) → Install / Update Toolbar. Confirm the included package installs; unrelated add-ons remain intact. If the SDK package builder started MSFS, close it before installing.
3. Start MSFS 2024, load a cockpit, open AeroScience from the toolbar and confirm fresh actual aircraft values. Do not use the old simulated preview as evidence.
4. Start a short live mission, fly briefly, end it and inspect saved samples/history. Exit/relaunch AeroScience and confirm the mission remains. Record which aircraft, app/commit, Windows, MSFS and SDK versions were used.
5. Check native dependencies and the graphical Visual C++ prerequisite route on supported Windows. The end user must need no SDK or manual DLL copying.
6. Confirm the candidate ZIP extracts, the checksum matches, included files are complete, and it contains no personal databases/logs/secrets. The build already performs structural/CRC/hash checks; record your extracted-app inspection as well.

Copy the tested candidate's `ACCEPTANCE.json` to `C:\AeroScience-Release-Records\acceptance-v0.1.1-alpha.json`. Open the copy in Notepad. Leave its generated `version`, `source_commit` and `payload_sha256` unchanged. Enter a tester label, actual ISO timestamp with timezone, and genuine evidence for each of the seven required checks. The template includes plain-language instructions beside each check. Set `result` to `passed` only for actual passes; otherwise record failed/blocked/pending and stop final publication.

Mach comparison, sustained cadence, aircraft variety, migration edge cases, iPad and VR testing should continue during the alpha. They may remain pending for this prerelease and must remain identified as unverified. Stable releases retain the extended acceptance gates. A cross-build, unit test or old v0.1.0 screenshot cannot count as a new Windows hardware pass.

Do not rebuild after acceptance: the generated payload fingerprint binds your test record to every included operational file. Changes require a new candidate/test record. The app stores test missions outside the installation, so they are not bundled.

## 5. Finalize the exact tested bytes

From `C:\Dev\AeroScience-release`, after minimum checks really pass:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\New-Release.ps1 -AcceptanceRecord 'C:\AeroScience-Release-Records\acceptance-v0.1.1-alpha.json'
```

This does not rebuild. It verifies the recorded revision/payload, runtime permission/loading, SDK hashes and minimum acceptance, removes the candidate marker, creates the final ZIP/checksum, re-extracts/verifies them and generates release notes.

**Upload these two final files:**

```text
C:\Dev\AeroScience-release\artifacts\AeroScience-v0.1.1-alpha-win-x64.zip
C:\Dev\AeroScience-release\artifacts\AeroScience-v0.1.1-alpha-win-x64.zip.sha256
```

Release notes are at `artifacts\RELEASE-NOTES.md`. The printed SHA-256 and sidecar describe this final ZIP, not an older companion preview. Existing releases/checksums are not overwritten. Copy the recorded source commit from `acceptance-v0.1.1-alpha.json` for the tag step.

## 6. Create the public GitHub prerelease manually

First ensure the tag points to that exact tested commit. In the maintainer checkout, replace the placeholder below with its 40-character `source_commit`:

```powershell
git tag v0.1.1-alpha TESTED_40_CHARACTER_COMMIT
git push origin refs/tags/v0.1.1-alpha
```

These commands create only the source tag. They do not upload binaries or publish a release. If the tag already exists, stop and check it points to the tested commit; do not force-move it. Git for Windows may open its normal browser sign-in. No special release token is needed for manual website upload.

1. Visit **https://github.com/JustSeanC/AeroScience/releases** and sign in as a maintainer.
2. Choose **Draft a new release**. Under **Choose a tag**, select the existing **v0.1.1-alpha** tag just pushed. This avoids publishing whatever happens to be the latest main commit.
3. Set the title to **AeroScience v0.1.1 — Live Flight Recording Alpha**.
4. Paste `artifacts\RELEASE-NOTES.md` into the description. Retain known limitations and the checksum. The notes identify the exact source revision.
5. Drag in the **final ZIP and its .sha256 file**, not CANDIDATE files or a companion-only preview. Wait until both uploads finish.
6. Check **Set as a pre-release**. Do not mark it stable/latest production. Review the tag, files and notes, then click **Publish release**.
7. Open the release in a signed-out/incognito browser. Download both assets, extract the ZIP and compare its SHA-256 to the sidecar. Confirm the ordinary public download works. Keep the original ZIP and test/SDK/runtime evidence.

After actual successful publication, the intended URLs are:

- Release: https://github.com/JustSeanC/AeroScience/releases/tag/v0.1.1-alpha
- Windows ZIP: https://github.com/JustSeanC/AeroScience/releases/download/v0.1.1-alpha/AeroScience-v0.1.1-alpha-win-x64.zip
- Checksum: https://github.com/JustSeanC/AeroScience/releases/download/v0.1.1-alpha/AeroScience-v0.1.1-alpha-win-x64.zip.sha256

These are expected destinations, not a claim that files currently exist. Do not create a second conflicting release or overwrite a published asset. Fixes after publication should use a new source version/tag.

## Testers and future automation

Send friends the public release page and tell them to download the Windows ZIP under Assets. They extract it, launch AeroScience.exe and install the included toolbar through Settings. They do not use GitHub Actions artifacts or Source code ZIP downloads.

The optional Actions workflow can later prepare candidates on a trusted SDK Windows runner and publish a previously tested candidate from a GitHub-hosted Windows runner using the normal GitHub token. It is not part of this first-release path. Version/tag/asset metadata comes from `Directory.Build.props`; update it and the numeric toolbar definition for future versions. Prior releases remain downloadable; no automatic updater is introduced.
