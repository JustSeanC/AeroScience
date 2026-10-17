# Public release process

**For the first alpha, use [the Windows gaming-PC build and manual GitHub upload guide](public-release-windows.md). No self-hosted runner is required.** Ordinary testers download one complete Windows ZIP from Releases, extract it and open AeroScience.exe.

The user has approved public prerelease distribution in `JustSeanC/AeroScience`. It is no longer restricted to a private destination. No full release has yet been published: specific native-runtime redistribution permission and the new Windows SDK/GUI/live smoke checks remain outstanding. See [runtime-distribution.md](runtime-distribution.md).

## Prepare, test, finalize, publish

1. Use a clean Git checkout and the existing official Windows SDK. `Build-ReleaseCandidate.ps1` builds/tests the app, compiles the installed-header ABI contract, runs Microsoft's package builder and structural checks, validates the approved runtime's actual Windows loading/exports, and prepares a complete **CANDIDATE** ZIP plus checksum.
2. Extract and test that candidate on the real gaming PC. Its generated `ACCEPTANCE.json` includes version, exact source commit and payload fingerprint. Record genuine passes for the seven minimum checks. Optional extended checks may remain pending for the prerelease and are disclosed; stable releases require extended acceptance.
3. `New-Release.ps1` finalizes without recompiling. It validates unchanged operational bytes, SDK receipts/source, runtime approval/loading and acceptance, then verifies final ZIP extraction/CRC/SHA-256. It generates concise notes for the exact tested revision. Original candidates, existing ZIPs and checksums are not overwritten.
4. Use the GitHub Releases website to create **v0.1.1-alpha** at that exact tested commit, mark it **Prerelease**, and upload the final ZIP/checksum. No CLI publication, Actions artifact access or special token is needed by this manual route. Verify downloads signed out before sharing the link.

For a checkout at `C:\Dev\AeroScience-release`, final assets are:

```text
C:\Dev\AeroScience-release\artifacts\AeroScience-v0.1.1-alpha-win-x64.zip
C:\Dev\AeroScience-release\artifacts\AeroScience-v0.1.1-alpha-win-x64.zip.sha256
C:\Dev\AeroScience-release\artifacts\RELEASE-NOTES.md
```

The complete ZIP contains one folder:

```text
AeroScience-v0.1.1-alpha-win-x64/
  AeroScience.exe
  application DLLs and self-contained .NET/Windows Forms/ASP.NET runtime assets
  SimConnect.dll                       # Specific permission and validated official native file required
  wwwroot/                             # Complete dashboard
  Toolbar/aeroscience-toolbar/          # Genuine SDK-built Community package
  Toolbar/sdk-build.json
  Dependencies/runtime-check.json       # Sanitized actual Windows loading/export report
  Dependencies/runtime-approval.json    # Public redistribution evidence/attestation
  build-info.json, DEFAULTS.json, ACCEPTANCE.json
  README.md, INSTALL.md, CHANGELOG.md, LICENSE, THIRD-PARTY-NOTICES.md
  docs/alpha-testing.md, docs/runtime-distribution.md
```

`DEFAULTS.json` documents built-in defaults; settings are managed graphically, not by editing it. No personal settings/database/logs/credentials, source trees, PDBs, developer scripts or test executables are included. Storage remains outside the install folder and existing missions are retained. Candidate/final checks compare every operational file; acceptance/status documents alone may change after testing.

## Minimum versus extended acceptance

Prereleases require `windows_companion`, `offline_archive_export`, `toolbar_install_load`, `simconnect_live`, `live_mission_smoke`, `runtime_dependencies` and `release_contents` with real evidence. SDK/ABI provenance, version/layout validation, clean matching source, specific runtime permission and actual runtime loading/exports remain mandatory regardless of release tier.

Stable releases additionally require recovery, storage migration, live Mach, toolbar synchronization, VR performance and iPad checks. Alpha notes disclose extended checks left pending. Never relabel portable fixtures/cross-compilation or an old v0.1.0 screenshot as a new hardware pass. The example record deliberately cannot pass.

## Optional future GitHub Actions flow

`.github/workflows/alpha-release.yml` is manual and optional:

- **prepare-candidate:** on an existing trusted Windows x64 SDK runner labeled `msfs2024-sdk`, build the official candidate with documented runtime permission. Upload it as a candidate Actions artifact for maintainer testing. It does not publish a release or mark hardware acceptance passed.
- **publish-tested-candidate:** provide the earlier candidate's exact source commit, run ID and completed genuine acceptance JSON. A GitHub-hosted Windows runner downloads that same candidate, finalizes without rebuilding, and publishes to this repository. No SDK runner is required for this publication stage. The standard `github.token` has `contents: write` and `actions: read` only in this job; no private repository/token is needed.

A maintainer must test a downloaded candidate before its publication stage; rebuilding and reusing stale evidence is refused by the fingerprint. The publisher checks public repository identity and tag target, starts with a draft, refuses conflicting assets, resumes matching drafts and verifies unauthenticated public downloads. Reruns do not replace published assets. A later fix uses a new version/tag so historical releases remain intact. The optional publisher is covered by mocked control-flow tests, not a claimed live GitHub publication.

No runner is configured during this task, and there is no requirement to register one before the first manual release. Public push/PR CI still runs checks without publishing binaries.

## Future versions and local previews

Version metadata, tag, platform ZIP name and prerelease status derive from `Directory.Build.props`; update the numeric toolbar package version too. App/toolbar source and receipt versions must match. Update changelog/test evidence for the new version. Stable versions have no suffix; alpha/beta/RC versions remain prereleases. Do not move an already published tag or overwrite a historical ZIP. No automatic updater is introduced.

`Build-Companion.ps1` can still produce a companion-only preview. The preview packager rejects the proprietary runtime and clearly labels its ZIP as Preview; it lacks the full toolbar/runtime and cannot be uploaded as the requested complete alpha. Linux cross-builds validate source/build capability, not Windows installation or simulator acceptance.
