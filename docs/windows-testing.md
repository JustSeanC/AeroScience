# AeroScience v0.1.0: Windows installation and live-testing workbook

This workbook completes v0.1.0 installation/testing before any v0.1.1 closed-alpha release automation. No Windows, simulator, headset or physical-iPad result is marked passed by these instructions. The existing three commits are **already pushed** to `https://github.com/JustSeanC/AeroScience`, branch `main`.

## 1. Download, verify, extract and launch the companion preview

Download the published Windows companion from **GitHub Actions**, not a cloud-workspace file link:

1. Sign in to GitHub in your browser.
2. Open [successful build #2 for commit `0818a64`](https://github.com/JustSeanC/AeroScience/actions/runs/38923289277#artifacts).
3. Scroll to **Artifacts**, then click **Windows-Companion-unverified-in-MSFS** (about 49 MB). Your browser downloads `Windows-Companion-unverified-in-MSFS.zip`.

GitHub requires sign-in to download Actions artifacts, even for this public repository. If the artifact is not clickable, check that you are signed in. Artifacts expire according to repository retention settings; if this build has expired, use a newer successful [foundation build](https://github.com/JustSeanC/AeroScience/actions/workflows/ci.yml) with the same artifact. A workspace file link opens the cloud editor and does not attach the ZIP to chat. GitHub's **Code → Download ZIP** contains source; it does not contain the published executable. There is no public GitHub Release asset yet.

You can download on the work computer and transfer the ZIP to your gaming PC later. Run the Windows application on the gaming PC. The preview is self-contained Windows x64: it includes .NET, the SQLite library and the hosted dashboard. You do not need Visual Studio, a .NET SDK, Node or MSFS to run simulated mode.

In ordinary Windows PowerShell, display the downloaded ZIP's checksum (adjust the path if your browser renamed it) and compare it with the **Digest** shown beside that artifact on GitHub. Stop if they differ. The Actions artifact has its own digest; the separately generated cloud-workspace preview ZIP and its sidecar are a different archive.

```powershell
$zip = Join-Path $env:USERPROFILE 'Downloads\Windows-Companion-unverified-in-MSFS.zip'
Get-FileHash -LiteralPath $zip -Algorithm SHA256
```

Then extract and launch:

```powershell
$destination = Join-Path $env:USERPROFILE 'Games\AeroScience-0.1.0-Test1'
Expand-Archive -LiteralPath $zip -DestinationPath $destination
Set-Location $destination
.\Launch-Simulated.cmd
```

Use a new extraction folder for each build. Explorer's **Extract All** is also supported. Keep every extracted file together and run the extracted files, not files still inside the ZIP. Expect `AeroScience.Engine.exe`, runtime DLLs, `wwwroot`, both launch CMDs, `Collect-Feedback.ps1` and `Test-SimConnectRuntime.ps1` directly in the extraction folder. A separately generated `AeroScience-Companion-Preview-v0.1.0-win-x64.zip` has an additional `AeroScience-Companion-Preview-v0.1.0/Windows-Companion` folder; locate its executable and launcher there instead.

Leave the console open. Open `http://127.0.0.1:5080` in Edge or Chrome on the same PC. Expect the version, **SIMULATED TELEMETRY**, an online engine and the synthetic ER-2 trajectory. Its aircraft title is a test fixture, not an aircraft restriction. Altitude, position and sample timestamps should change; synthetic IAS can stay constant. There is no tray application or automatic browser launch yet.

Use the same normal Windows account for each launch so the same LocalApplicationData history is used. Only the optional firewall-rule command requires elevation.

The updated simulated launcher explicitly disables the MSFS bridge and LAN listener; it does not need `SimConnect.dll` and should not report missing simulator dependencies. Application data stays in `%LOCALAPPDATA%\AeroScience`, separately from the extracted application folder. If Windows blocks an unsigned file, record the exact dialog/error and the verified ZIP hash; no code-signing claim is made for this preview.

## 2. Complete the simulated-mode checklist first

Use expendable test missions with recognizable names:

1. Open Mission Control. Confirm Universal is the initial selection and generated data is clearly identified.
2. Start `SIM-Universal-01`. Wait at least 60 seconds. Expect approximately one recorded sample per second, a running duration and changing position/altitude.
3. Open a second browser tab to the same address. Both displays should show the same active mission, mode and sample count. Close the first tab; recording must continue in the second.
4. End the mission. Open Mission History → View mission. Check Completed, Universal, aircraft/source, duration, sample count, start/end UTC, departure/arrival and altitude range.
5. Open **Inspect telemetry JSON**. For this short test, the first-5,000-samples view contains the full recording. Check increasing UTC timestamps, changing coordinates, finite speeds/attitude and `source: "simulated"`. Save this JSON only if you want to share that test flight.
6. Create `SIM-Realistic-01`. Check the future-features explanation. Start/end it, and verify Realistic in both tabs and history. Both science modes record identically in 0.1.0; there are no sensors, calibration, restrictions or data-quality penalties yet. The next new mission form should return to Universal.
7. With no active mission, stop the companion using Ctrl+C in its console. Relaunch it from the same folder, reload the dashboard and verify both completed records/modes are retained.
8. Optional interruption test: start `SIM-Interrupted-01`, wait for samples, then end **only `AeroScience.Engine.exe`** through Task Manager. Relaunch. Expect an **Interrupted** history record with committed samples, not an automatically resumed active mission. Leave the database files in place.

The available preview workflow includes telemetry, recording, history/detail, synchronized displays, restart recovery and mode persistence. Instruments, mapping, full replay, scientific measurements and offline PWA caching are not operational. Resolve any failures above before diagnosing real MSFS integration.

## 3. Retrieve the backed-up source and prepare Windows build tools

The three foundation commits are already on `origin/main`:

```text
13c1a7d  Implement AeroScience mission engine, dashboard, aircraft telemetry, and science modes
8325a2b  Reset new mission controls to Universal after recording
0268f74  Add reproducible builds, release gates, CI, and verification guidance
```

On the gaming PC, install Git for Windows, the .NET SDK version selected by `global.json`, Node 22+ (only for development checks), Python 3.11+ available as `python`, PowerShell 7 x64 (`pwsh`), and Visual Studio Build Tools with **Desktop development with C++**, MSVC x64 tools and a Windows SDK. The MSFS SDK is separate from those tools.

```powershell
git clone --branch main https://github.com/JustSeanC/AeroScience.git C:\Dev\AeroScience
Set-Location C:\Dev\AeroScience
git fetch origin
git log --oneline origin/main -8
git merge-base --is-ancestor 13c1a7d origin/main
if ($LASTEXITCODE -ne 0) { throw 'Foundation commit missing from the downloaded branch.' }
git merge-base --is-ancestor 8325a2b origin/main
if ($LASTEXITCODE -ne 0) { throw 'Mission-mode fix missing.' }
git merge-base --is-ancestor 0268f74 origin/main
if ($LASTEXITCODE -ne 0) { throw 'Build/release commit missing.' }
```

If you already have a Windows clone, run `git status` and preserve its changes, then `git pull --ff-only origin main` from `main`. These checks confirm backup; there is no need to recreate or recommit the three commits. For later changes you intentionally make on `main`, review `git diff`, stage the specific changed source/docs/scripts, `git commit -m 'Describe the tested change'`, then `git push origin main`. Verify `git rev-parse HEAD` matches `git ls-remote origin refs/heads/main`. Git for Windows can use its normal GitHub credential-manager sign-in; no token needs to be pasted into chat.

Keep SDK binaries, published builds, feedback bundles and mission databases out of Git. `artifacts/` and build outputs are ignored. A local data-folder backup is separate from Git; see section 9.

Build the companion from source only when needed:

```powershell
pwsh -NoProfile -File .\scripts\build\Build-Companion.ps1
```

Its self-contained output is `C:\Dev\AeroScience\artifacts\Windows-Companion`. Build/testing dependencies are not required by the downloaded preview. Browser automation and MSFS hardware acceptance remain separate from that build script.

## 4. Identify and check your official SDK/runtime

On the gaming PC, confirm the SDK installed from **MSFS 2024 Developer Mode → Help → SDK** and record its actual installer/SDK version. “Latest” alone is not a reproducible version. Use the current SDK matching your simulator, with the CommBus APIs (reference evidence identifies SDK 1.6.4+ / SU5+, but installed headers and exported symbols are the check).

In PowerShell 7, set the actual SDK location:

```powershell
$sdk = 'C:\MSFS 2024 SDK' # Replace with your installed folder.
Get-Item -LiteralPath (Join-Path $sdk 'SimConnect SDK\include\SimConnect.h')
Get-Item -LiteralPath (Join-Path $sdk 'Tools\bin\fspackagetool.exe')
Get-ChildItem -LiteralPath (Join-Path $sdk 'SimConnect SDK') -Filter SimConnect.dll -Recurse -File |
    Select-Object FullName, Length
```

Choose the SDK's **native x64 `SimConnect.dll`**, not `Microsoft.FlightSimulator.SimConnect.dll` (the older mixed-mode .NET Framework wrapper), an x86 DLL or an unofficial download. Use the path from your installed SDK's files, not a guessed location. If no native runtime is found there, consult that installed SDK's SimConnect runtime documentation and report the SDK version/file layout before substituting another file.

```powershell
$native = 'C:\REPLACE\WITH\THE\SDK\NATIVE\x64\SimConnect.dll'
pwsh -NoProfile -File .\scripts\build\Test-SimConnectRuntime.ps1 -RuntimePath $native -ReportPath .\artifacts\runtime-check.json
```

The checker rejects wrong PE architecture and managed wrappers, then on Windows x64 loads the DLL and checks every native export used by telemetry and CommBus. A successful check establishes **local loading/export availability**, not a simulator connection, redistributability or full ABI compatibility. `-MetadataOnly` is only a PE inspection and must not be recorded as a runtime pass.

If the DLL is present but loading fails, its dependencies may be missing. Follow the installed SDK's runtime guidance; if it requires the Microsoft Visual C++ v14 x64 runtime, obtain the supported redistributable from [Microsoft's official guidance](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). Do not replace SDK files with downloaded third-party DLLs. .NET itself is already in the self-contained preview.

For personal live testing, stop the companion and copy that checked native DLL beside the **particular** `AeroScience.Engine.exe` you will run:

```powershell
$app = 'C:\REPLACE\WITH\YOUR\EXTRACTED\Windows-Companion'
Copy-Item -LiteralPath $native -Destination (Join-Path $app 'SimConnect.dll')
```

Keep this personal live-test folder separate from public preview inputs. The preview packager now refuses inputs containing the proprietary runtime. Public redistribution remains subject to Microsoft's actual SDK terms, which have not been verified in this cloud environment.

## 5. Check live telemetry before introducing the toolbar

1. Close the synthetic companion. Start MSFS 2024 normally on the same PC, load a default aircraft, and enter a flight/cockpit. Menus/loading are not a valid telemetry session.
2. From the checked companion directory, run:

   ```powershell
   .\AeroScience.Engine.exe --AeroScience:Telemetry=SimConnect --AeroScience:EnableToolbarBridge=false --AeroScience:EnableLan=false
   ```

3. Open the local dashboard. Expect the real loaded aircraft title and a simulator source. The provider must not silently substitute synthetic data if MSFS is unavailable. Check the connection message and logs if it remains waiting/disconnected.
4. While stationary, compare latitude/longitude, MSL/AGL and heading with the simulator. Then taxi/fly and compare IAS/TAS, groundspeed, true heading/ground track, climb rate, pitch and bank. The display uses nose-up/right-bank positive conventions. Heading and track are true, not magnetic; exact instantaneous agreement with a magnetically referenced cockpit indicator is not expected.
5. Record `LIVE-Default-01`, change altitude/heading, then end it. Inspect history and saved telemetry: `source: "simulator"`, matching aircraft, changing positions/attitude and UTC samples around the configured one-second rate.
6. Repeat as separate named missions with a default fixed-wing aircraft, a freeware/payware aircraft you own, and a helicopter. Check stationary hover/zero airspeed. Realistic mode must remain recordable on an unknown aircraft; an unknown title uses a fallback and nonfinite AGL becomes unavailable rather than blocking the other data. There is no whitelist or individual aircraft modification.
7. In an expendable mission, test pause/resume, menus/flight restart and aircraft changes. Look for waiting/fresh states and aircraft-change/connection events. Missing/stale data must not be filled with invented frames. Some pause modes may still produce current telemetry; record the actual pause mode and behavior.
8. With an expendable mission active, close/restart MSFS while leaving the engine running. Expect disconnect/retry and the same active mission after connection returns; recording gaps remain gaps. End the mission from the dashboard. Engine-process interruption is a separate test and archives the mission as Interrupted on restart.

Basic local SimConnect needs no LAN listener, cloud credential or SimConnect network configuration file. Leave simulator/companion on the same PC and use the default local connection for these tests.

## 6. Compile and install the toolbar proof of concept, then full panel

Both diagnostic and full panels loaded on the gaming PC. Retesting `9a1946d` still failed normal dragging/resizing; left-edge dragging translates the whole window despite a resize cursor. Latest scrolling is unconfirmed. Use the [native-window probe/full builds, capture and desktop checklist](toolbar-interactions.md) before another speculative correction. Reuse `C:\Windows-Companion-unverified-in-MSFS`. New official SDK compilation and native results are unverified.

Open **x64 Native Tools Command Prompt** for your installed Visual Studio Build Tools. From that prompt launch `pwsh -NoProfile` so PowerShell inherits the x64 compiler environment. No administrator session is needed for ordinary builds.

```powershell
Set-Location C:\Dev\AeroScience
$sdk = 'C:\MSFS 2024 SDK' # Replace with your actual folder.
Get-Command cl.exe, python, pwsh
pwsh -NoProfile -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot $sdk -ProofOfConcept
```

Start with MSFS closed for an isolated command-line build. The script stages source under `artifacts\toolbar-poc`, compiles `sdk-contract.cpp` against your official headers, and invokes the official package builder. The contract checks x64 layout, receive IDs, data types, message sizes and typed API calls used by the native client. A mismatch stops the build; keep the report instead of changing constants/checksums until they match an unsupported assumption.

It retains `sdk-contract.log` and `build.log`, validates the builder's generated package, and issues `sdk-build.json` only after success. It prints the **actual package directory** containing `manifest.json`. Default SDK tool/header locations can be overridden if your installation differs:

```powershell
pwsh -NoProfile -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot $sdk -ProofOfConcept `
    -PackageToolPath 'C:\ACTUAL SDK LOCATION\fspackagetool.exe' `
    -HeaderDirectory 'C:\ACTUAL SDK LOCATION\SimConnect headers'
```

If an older checkout fails with `error C2607: static assertion failed` at the line `sizeof(SIMCONNECT_RECV_COMM_BUS) == 32` in `scripts/build/sdk-contract.cpp`, update that file from the repository before retrying. The 32-byte value describes the offset of `rgData`, not the size of a struct that includes its variable-length string member. The corrected contract checks the field offsets used by the companion, including `rgData` at byte 32. Keep the checks in place; a failure of the corrected checks requires inspecting the installed header and compiler output.

If Microsoft's builder reports `Validation SUCCESS` followed by our checker reporting `SDK minimum_game_version is missing/invalid`, inspect the SDK-generated `manifest.json`. The first gaming-PC build produced `1.9.18`; an older checker incorrectly required four version components. Update `scripts/release/package_release.py` from the repository and rerun the build. The corrected checker accepts three or four numeric components and prints the offending value if it is missing/malformed. Keep the generated manifest as SDK output; do not edit its version to satisfy the old checker.

If the next error is `Layout references a missing or invalid file: html_ui/icons/toolbar/icon_aeroscience.svg`, compare the layout path with the actual file. The observed SDK output lowercases every layout path while preserving mixed-case filenames such as `ICON_AEROSCIENCE.svg` and `AeroScience.html`. Update `scripts/release/package_release.py` and rerun the build. The corrected checker matches all package paths case-insensitively, retains actual file names for receipts, and still rejects genuinely missing files, size mismatches and duplicate/colliding paths. Do not rename SDK output or edit `layout.json` to work around the old checker.

If the installed package tool requires a simulator-assisted build, use Developer Mode's Project Editor → Open project on `C:\Dev\AeroScience\artifacts\toolbar-poc\AeroScience.xml` and **Build All In Project**, retaining its errors/output. That is a distinct build route; it does not automatically create the command-line script's receipt. Do not fabricate a receipt or copy source files into Community to bypass a failed build.

Locate your **configured** Community and Community2024 locations in MSFS Developer Mode: **Tools → Virtual File System → Packages Folders**, then **Open Community Folder** or **Open Community 2024 Folder**. These are the labels documented in the [official SDK overview](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_Overview.htm#community). Store and Steam differ, and either can use a custom drive. For the first installation test of this legacy `InGamePanels`/SPB project, try the regular **Community** compatibility folder. That is a test candidate, not a claimed registration pass. Community2024 support remains pending. Use one copy of the package, not duplicates across both folders. Keep Explorer open on the chosen destination and close MSFS before copying the package.

1. With MSFS stopped, copy the **SDK-built `aeroscience-toolbar` directory** printed by the script into that Community location. The result must be `...\Community[2024]\aeroscience-toolbar\manifest.json`, not an extra nested package folder.
2. Check the sibling `layout.json`, `InGamePanels\aeroscience_panel.spb` and `html_ui` assets are present. These must be builder output; the source checkout alone is not installable.
3. Launch MSFS, load a flight, enable AeroScience in toolbar customization and open its panel.
4. For the read-only transport probe, launch the checked companion with:

   ```powershell
   .\AeroScience.Engine.exe --AeroScience:Telemetry=Simulated --AeroScience:EnableToolbarBridge=true --AeroScience:EnableLan=false
   ```

5. Expect changing JSON replies in the POC panel. Its data is intentionally synthetic; this step tests registration/CommBus, not real aircraft telemetry. The normal simulated launcher disables CommBus and is not the probe launcher. `Waiting for SDK communication service…` is the panel's initial text and can remain visible with no companion/reply; seeing it confirms static panel rendering, not that the JavaScript service or native bridge is connected. Check the companion console for `MSFS toolbar CommBus connected (SU5+); waiting for panel requests`, then require actual changing replies to establish end-to-end transport.
6. If registration and transport work, stop MSFS and build the full project:

   ```powershell
   Set-Location C:\Dev\AeroScience
   pwsh -NoProfile -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot $sdk
   ```

7. Preserve the old test package outside Community if you need it, replace the installed POC with the full SDK-built output and restart MSFS. The package ID is the same, so only one variant can be installed at a time.
8. First run the companion with `Telemetry=Simulated`, `EnableToolbarBridge=true` and `EnableLan=false` using the explicit executable command above. Verify the full panel shows **SIMULATED TEST DATA**, changing readings, mode selection and Mission/Payload navigation. Scientific payloads remain unavailable.
9. Start a Universal mission in the toolbar. Confirm the browser shows the same active mission; close/reopen the toolbar and confirm recording/sample count continues. End it from the browser and confirm the toolbar returns to standby. Repeat with a Realistic mission started in the browser and ended in the toolbar; verify saved modes/history.
10. After simulated full-panel controls/synchronization pass, stop the companion and use `Launch-SimConnect.cmd` from the same checked companion directory. Its default enables the bridge and keeps LAN off. Repeat with real aircraft telemetry, confirming the live source and actual loaded aircraft before recording. Follow section 5's aircraft/lifecycle checks; synthetic success does not establish these.

The transport uses simulator-owned `CommBus.js` / `RegisterCommBusListener`, native `SimConnect_SubscribeToCommBusEvent` and `SimConnect_CallCommBusEvent`. It does not embed the dashboard or access localhost sockets in Coherent. Official documentation was accessible during the first Windows build troubleshooting and confirmed the CommBus variable-length data member. The gaming-PC header/runtime checks and actual SDK/Coherent tests remain required evidence, not implied passes.

## 7. VR performance test only

After the desktop toolbar functionality passes, establish a working VR session and record the headset connection, OpenXR runtime, resolution/graphics and simulator settings. Use one responsive toolbar interface; no dedicated VR display mode exists.

Measure equal intervals with the companion stopped/panel closed, companion running/recording/panel closed, and companion running/recording/panel open. Repeat opening/closing and record simulator FPS/frame times, stutters, companion CPU/memory and available toolbar/Coherent resource cost. Keep the flight/settings constant, stop debugger captures during measurements and note measurement tools. See [performance-only guidance](toolbar-interactions.md#vr-performance-only). No separate Quest 3 controller, resize, scroll or layout-toggle matrix is required; desktop MSFS is the primary functional test. No VR performance result is established by cloud browser tests.

## 8. iPad mini 6 / trusted private LAN

This can also be tested with synthetic telemetry before MSFS. Use the **direct executable** for explicit LAN settings; the supplied launchers keep LAN off by default.

On the gaming PC, find the active Ethernet/Wi-Fi private IPv4 address using `ipconfig` or `Get-NetIPAddress -AddressFamily IPv4`. Stop the existing engine, then from the checked companion directory:

```powershell
$lan = '192.168.1.25' # Replace with this PC's real private interface address.
.\AeroScience.Engine.exe --AeroScience:Telemetry=SimConnect --AeroScience:EnableToolbarBridge=true `
    --AeroScience:EnableLan=true "--AeroScience:LanAddress=$lan"
```

For a synthetic LAN test, change `Telemetry=Simulated` and use `EnableToolbarBridge=false`. The LAN address must be private (10.x, 172.16–31.x or 192.168.x); it is not an all-interface/public binding. Localhost stays available on the PC.

If needed, in a separate elevated PowerShell window add a rule for the **actual extracted executable** and your Private home-network profile only:

```powershell
$engine = 'C:\REPLACE\WITH\YOUR\Windows-Companion\AeroScience.Engine.exe'
New-NetFirewallRule -DisplayName 'AeroScience testing 5080' -Direction Inbound -Action Allow `
    -Program $engine -Protocol TCP -LocalPort 5080 -Profile Private -RemoteAddress LocalSubnet
```

On the PC's localhost dashboard, open Settings and copy the pairing code. On iPad Safari open the displayed `http://PC-PRIVATE-IP:5080` address, enter the code, and check source/mode, live telemetry, start/end/history, portrait/landscape and touch targets. An incorrect code must be refused. Closing Safari/Wi-Fi should leave engine recording; reconnect and confirm the same mission. Engine restart changes pairing/session keys, so pair again. Use a trusted private LAN: this initial HTTP setup does not encrypt traffic and is not a public-internet service.

Record iPadOS/Safari version, iPad model, orientation and exact errors. Guest Wi-Fi isolation, a Public firewall profile or selecting a VPN/wrong interface can prevent access. Remove the testing firewall rule when finished if no longer needed: `Remove-NetFirewallRule -DisplayName 'AeroScience testing 5080'`.

## 9. Logs, feedback, data preservation and acceptance

The primary log directory is `%LOCALAPPDATA%\AeroScience\logs`; filenames use UTC, with at most seven daily logs and 5 MB per log. Startup, source, reconnect and SDK errors also appear in the console. If `DataDirectory` was overridden, pass that same folder to the collector.

From `Windows-Companion`, built-in Windows PowerShell can create a desktop feedback ZIP:

```powershell
powershell -NoProfile -File .\Collect-Feedback.ps1
```

For build/loader reports, first place `runtime-check.json` beside the POC/full build logs you want to collect, then use:

```powershell
powershell -NoProfile -File .\Collect-Feedback.ps1 `
    -BuildLogDirectory 'C:\Dev\AeroScience\artifacts\toolbar-poc'
```

The collector includes selected text logs/reports, version/hash information and a feedback template. It excludes databases, telemetry exports, SDK binaries, credential/environment dumps and pairing-code API responses. It redacts the Windows user-profile prefix in copied text. No report is uploaded. Extract/review the ZIP, complete `FEEDBACK.txt`, and share the reviewed report and screenshots without pairing codes. Ordinary engine logs never log pairing codes/session bodies; review any manually added console/build text as well.

If the app fails before creating a log, capture the direct executable in PowerShell:

```powershell
.\AeroScience.Engine.exe --AeroScience:Telemetry=Simulated --AeroScience:EnableToolbarBridge=false --AeroScience:EnableLan=false 2>&1 |
    Tee-Object -FilePath .\startup-console.txt
```

Provide the exact error/HRESULT, UTC time, MSFS/SDK version, Store/Steam, aircraft/add-on version, source, science mode, steps, expected/actual behavior, mission ID/count, and VR/iPad details when applicable. Save the SDK's own report and Coherent console errors when a toolbar build/load fails. Share a short chosen test mission JSON separately if needed; the UI export is capped at 5,000 samples, with API paging for larger recordings.

To preserve all history, stop the engine and back up the **entire** `%LOCALAPPDATA%\AeroScience` directory, including any WAL/SHM files, before updating. Extract replacement application files into a new folder; keep the data directory. Normal update/uninstall must not delete it. Git backups preserve code, not this user-data directory.

Record every hardware result as pending until you run it. Use `release-acceptance.example.json` as the checklist, enter your actual version/date/evidence, and keep failures/pending items distinct. This workbook does not produce or publish `AeroScience-v0.1.0.zip`; full release acceptance and permitted runtime distribution are still prerequisites. Automated friends-only GitHub Releases belong to the later v0.1.1 work.
