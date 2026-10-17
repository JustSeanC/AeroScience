# Build and private alpha release

The v0.1.1-alpha deliverable is one extracted application folder. Testers open **AeroScience.exe**; it starts the local engine, dashboard and CommBus services. Settings installs the supplied SDK-built toolbar into the selected Community folder while MSFS is closed. Testers need no developer tools or terminal commands. See [INSTALL.md](../INSTALL.md).

A complete alpha has not been produced in this cloud environment. Windows GUI execution, the extended official-SDK contract/build, live Mach, sustained recording, removable-drive behavior and device acceptance remain pending. Cross-publishing is not Windows execution. The runtime redistribution route must also be established before distributing the native Microsoft DLL.

## Maintainer prerequisites

Use a trusted Windows x64 machine with the pinned .NET SDK from `global.json`, Node 22+, Python 3, PowerShell 7, Visual Studio C++ build tools and the official MSFS 2024 SDK. Run SDK builds from an x64 Native Tools shell so `cl.exe` is available. These are maintainer requirements, not tester installation steps.

Commit the source before building a release. The app and toolbar must identify the same clean source revision. Keep SDK binaries, personal databases, logs and settings outside source control. Review the installed SDK runtime license and supported Visual C++ prerequisites; public API documentation alone does not establish redistribution permission.

## Application and SDK package

From the repository root in PowerShell:

```powershell
pwsh -NoProfile -File .\scripts\build\Build-Companion.ps1
pwsh -NoProfile -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot 'C:\MSFS 2024 SDK'
```

The companion script restores locked dependencies, tests the solution, checks web JavaScript and publishes the self-contained Windows Forms app to `artifacts/Windows-Companion`. It removes legacy developer launchers/extra executable entry points and writes `build-info.json` with version, target, revision and working-tree state. Retain all runtime libraries and notices with AeroScience.exe.

The toolbar script compiles the extended native ABI contract against installed official headers, runs Microsoft's package builder and validates the generated Community files. It retains `sdk-contract.log` and `build.log`, then writes `artifacts/toolbar-full/sdk-build.json` with source revision, header/builder hashes and every output file hash. Diagnostic POC/window-probe variants cannot pass the full release gate. No SPB, layout or manifest is manufactured by our release scripts.

For personal developer testing, Settings can import the official native x64 SimConnect.dll from an official installation after checking architecture and required exports. This is not a substitute for an approved end-user runtime delivery route. The maintainer's checker is:

```powershell
pwsh -NoProfile -File .\scripts\build\Test-SimConnectRuntime.ps1 -RuntimePath 'C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll'
```

## Acceptance and assembly

Complete [alpha-testing.md](alpha-testing.md) and [verification.md](verification.md) on the actual machines. Copy `docs/release-acceptance.example.json` to a local record and enter real tester/date/evidence for every required check. All example checks intentionally remain pending. Browser fixtures, native header compilation and structural validation do not establish simulator or hardware acceptance.

After the permitted official runtime is present beside the published application, assemble with:

```powershell
pwsh -NoProfile -File .\scripts\release\New-Release.ps1 -CommunityPackage 'PATH TO SDK-BUILT aeroscience-toolbar' -SdkReceipt '.\artifacts\toolbar-full\sdk-build.json' -AcceptanceRecord 'PATH TO COMPLETED ACCEPTANCE.json' -RuntimeRedistributionConfirmed
```

The validator requires complete self-contained app files, no test/demo launchers or personal data, matching source provenance, real full-toolbar SDK output and hashes, valid layout coverage, no aircraft dependencies, completed acceptance and runtime permission confirmation. Existing ZIPs are not overwritten. Successful assembly writes `artifacts/AeroScience-v0.1.1-alpha.zip` and its SHA-256 sidecar:

```text
AeroScience-v0.1.1-alpha/
  AeroScience.exe
  AeroScience.dll, AeroScience.Engine.dll, runtime libraries, build-info.json
  SimConnect.dll                       # Only when distribution is permitted
  wwwroot/
  Toolbar/aeroscience-toolbar/          # Genuine SDK output
  Toolbar/sdk-build.json
  docs/, README.md, INSTALL.md, CHANGELOG.md
  LICENSE, THIRD-PARTY-NOTICES.md, ACCEPTANCE.json
```

Data lives separately in the selected storage directories; it is never bundled. Schema upgrades create a verified SQLite backup before changing existing v0.1.0 records. Explain rollback limitations in tester notes: the older executable cannot open the upgraded schema; retain the original/backup and never replace a newer archive blindly.

## Private release workflow

`.github/workflows/alpha-release.yml` is manual only. Configure an existing trusted self-hosted Windows x64 runner labeled `msfs2024-sdk`, with the official SDK, Python, GitHub CLI and x64 compiler environment. Dispatch from committed source and provide its installed SDK/runtime paths and completed acceptance record. Redistribution confirmation is required and defaults false.

Publication defaults off. To publish, explicitly select it, supply an existing **private** destination repository and bind an appropriately scoped token as `ALPHA_RELEASE_TOKEN`. The workflow checks the destination's actual private status before creating the prerelease. It does not create repositories, change visibility or invite testers. Ordinary public CI uploads no binaries. The workflow has not run; runner configuration, licensing, acceptance and private publication are still maintainer tasks.

## Companion-only developer preview

A separate preview may be assembled for personal GUI/offline testing. It deliberately excludes the Community package and proprietary runtime and cannot be described as the complete alpha. After `Build-Companion.ps1`, keep any personal runtime-enabled copy separate:

```powershell
python .\scripts\release\package_release.py companion-preview --companion .\artifacts\Windows-Companion --output .\artifacts\AeroScience-Companion-Preview-v0.1.1-alpha-win-x64.zip
```

Linux can cross-publish `src/AeroScience.Desktop` for `win-x64`, but must perform the same developer-entry cleanup and metadata generation before packaging. That produces Windows files for inspection, not Windows execution evidence. A workspace path is not a public download; distribute an approved artifact through the agreed private channel after review.
