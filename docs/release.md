# Build and release

The full release depends on Windows, the official MSFS SDK, a permitted SimConnect runtime distribution and real device acceptance. This environment cross-published the companion but cannot complete those external checks. There is intentionally no source-only or placeholder `AeroScience-v0.1.0.zip`.

## Companion

On Windows with the pinned SDK, Node 22+ and PowerShell 7:

```powershell
pwsh scripts/build/Build-Companion.ps1
```

This restores from lockfiles, runs the .NET tests, checks static web JavaScript and publishes all companion files to `artifacts/Windows-Companion`. The frontend needs no transpilation: those validated assets are copied into the publish output. End users of that output need no SDK or Node installation. Keep the self-contained runtime files and notices with the executable.

Linux can cross-publish the same target:

```bash
dotnet restore AeroScience.sln --locked-mode
dotnet publish src/AeroScience.Engine -c Release -r win-x64 --self-contained true --no-restore -o artifacts/Windows-Companion
cp scripts/release/Launch-*.cmd artifacts/Windows-Companion/
python3 scripts/release/package_release.py companion-preview --companion artifacts/Windows-Companion --output artifacts/AeroScience-Companion-Preview-v0.1.0-win-x64.zip
```

The preview is explicitly companion-only, lacks the Community package/SimConnect DLL and does not establish Windows execution. A SHA-256 sidecar accompanies it. Existing archives are never overwritten silently.

The preview packager rejects native/managed SimConnect runtime files in any input subfolder, ignoring filename case. Keep personal SDK-enabled live-test folders separate. It includes the CMD launchers, runtime checker and feedback collector even when the publish was produced directly with `dotnet publish`.

## Community / full release

1. Review official SDK/runtime licensing. Do not add proprietary binaries to source control. Establish the supported runtime distribution route and include the permitted x64 runtime beside the executable for the full release.
2. Build/test the toolbar POC, then the full SDK project with `Build-Toolbar.ps1`. It writes `sdk-build.json` beside the output, including the builder hash, UTC time, variant and package file hashes.
3. Run the platform checks in `verification.md`. Copy `release-acceptance.example.json` to a local acceptance record; enter actual tester, UTC date, passed results and concrete evidence. The example intentionally contains pending results and cannot pass packaging.
4. Assemble:

```powershell
pwsh scripts/release/New-Release.ps1 -CommunityPackage 'PATH TO SDK-BUILT aeroscience-toolbar' -SdkReceipt 'PATH TO toolbar-full/sdk-build.json' -AcceptanceRecord 'YOUR COMPLETED ACCEPTANCE RECORD.json' -RuntimeRedistributionConfirmed
```

The script validates the self-contained x64 PE executable, required runtime/files, SDK output, full-build hashes and recorded acceptance, then creates `artifacts/AeroScience-v0.1.0.zip` and its SHA-256 sidecar. It refuses POC/source-only packages, missing or mismatched layout files, aircraft dependencies, unsafe paths, altered SDK output, pending acceptance and missing redistribution confirmation. A successful archive contains:

```text
AeroScience-v0.1.0/
  Community/aeroscience-toolbar/      # Real SDK-built binaries/assets only
  Windows-Companion/                 # Published app, dashboard, runtimes, launchers
  README.md  INSTALL.md  CHANGELOG.md  LICENSE  THIRD-PARTY-NOTICES.md
  SDK-BUILD.json  ACCEPTANCE.json
```

Publication to GitHub Releases is a separate maintainer action; no release or external notification is sent by these scripts. User history lives under application data and is never bundled, overwritten, or deleted by packaging.
