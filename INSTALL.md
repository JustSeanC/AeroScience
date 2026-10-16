# Install and launch

Use the [Windows installation and live-testing workbook](docs/windows-testing.md) for the current copy/paste commands, simulated-mode checklist, SDK build, live flights, VR performance, iPad pairing, GitHub backup and feedback collection. This guide provides the shorter installation reference.

## Current companion preview

The companion-only preview ZIP is a self-contained Windows x64 publish, not the complete Community release. It includes the engine, dashboard, database library and .NET runtime. It excludes the Community toolbar binary and proprietary SimConnect runtime. Its Windows execution has not been tested in this Linux environment.

1. Sign in to GitHub, open a successful [Actions build](https://github.com/JustSeanC/AeroScience/actions/workflows/ci.yml), and scroll to **Artifacts**. Click **Windows-Companion-unverified-in-MSFS** to download its ZIP. [Build #2](https://github.com/JustSeanC/AeroScience/actions/runs/38923289277#artifacts) contains the Windows-readiness changes at `0818a64`. Workspace file links open the cloud editor; they are not download attachments. **Code → Download ZIP** contains source only.
2. Extract the entire archive to a new writable application folder. Keep all files together; do not run from inside the ZIP. GitHub's artifact ZIP places `AeroScience.Engine.exe` and the launchers directly in the extracted folder.
3. Open `Launch-Simulated.cmd` beside `AeroScience.Engine.exe`. No Visual Studio, .NET SDK or compilation is required for this published app. This launcher explicitly disables the simulator bridge and LAN for the initial standalone test. A separately generated companion-preview archive has an additional `AeroScience-Companion-Preview-v0.1.0/Windows-Companion` folder; locate the launcher there instead.
4. Open `http://127.0.0.1:5080` on that Windows PC. Verify the **SIMULATED TELEMETRY** label.
5. Choose Universal or Realistic, name the mission, start recording, wait for samples, and end the mission. Review Mission History and its detail view.
6. Keep the console running during recording. Closing a browser or toolbar display is safe. Closing the engine interrupts an unfinished mission and preserves committed samples.

New missions default to Universal. Both modes support any aircraft exposing compatible standard telemetry, including helicopters. Realistic Mode's advanced scientific restrictions are not implemented in 0.1.0; the mode is retained for future instrument behavior. Existing records without a mode become Universal during the database migration.

## MSFS / toolbar installation (pending an SDK-built release)

Do **not** copy `msfs-toolbar/PackageSources` into Community. It is source, not an installable package. The following steps apply only after a real package has been built and its pending checks completed.

1. Obtain the official x64 SimConnect runtime from the MSFS 2024 SDK on a development PC. See [runtime licensing](docs/simconnect.md). The maintainer must confirm redistribution terms before including it in a public release. Do not download a DLL from an unofficial site. End users of a future complete release should receive the permitted runtime with the app; the current preview has no live-runtime dependency installed for them.
2. Locate your simulator's configured package locations. Microsoft Store and Steam use different settings locations; custom package locations are supported. Use the simulator's installed-package configuration/Developer Mode package tools, rather than assuming a fixed installation drive or username.
3. The current toolbar project uses legacy `InGamePanels`/SPB packaging. Install the built `aeroscience-toolbar` folder in the **Community** location recognized for this package type. **Community2024 compatibility has not been verified**; do not claim it works there or duplicate the package across both folders. Use the configured Community folder where this panel already loaded; verify Community2024 separately before choosing it for distribution.
4. Restart MSFS after changing a toolbar package. Start the companion with `Launch-SimConnect.cmd`, load a flight, and wait for initialized aircraft telemetry. Menus/loading are not valid telemetry sessions.
5. Enable AeroScience in the toolbar customization list and open its floating panel. Start with the read-only proof of concept when developing the package. Then validate the full panel, mission mode, start/end commands, and synchronization with the browser.
6. Open/close the panel, change aircraft, restart flights, disconnect/reconnect the simulator, and ensure the mission is retained. Do this on representative default, third-party and rotary-wing aircraft.
7. Complete the [native-window comparison and desktop checklist](docs/toolbar-interactions.md). The known movement/resize failure remains a blocker; no corrected compiled package is supplied from the cloud. Reuse `C:\Windows-Companion-unverified-in-MSFS`. There is one responsive interface and no Compact VR control. VR-specific testing measures performance only, using the comparison in that guide.

## iPad / private LAN

Localhost is the default. LAN access is opt-in and binds only to a selected private IPv4 address.

From PowerShell on the Windows PC, before launching the companion:

```powershell
$env:AeroScience__EnableLan = 'true'
$env:AeroScience__LanAddress = '192.168.1.25' # Replace with this PC's actual private IPv4 address.
.\AeroScience.Engine.exe
```

Allow the application port (5080 by default) on the **Private** Windows firewall profile for the local subnet, if required. No firewall rule is installed automatically. Do not open the port on the Public profile or forward it through a router.

On the PC's localhost dashboard, open Settings and copy the pairing code. Open the displayed LAN URL on iPad Safari and enter that code. Codes change on engine restart; sessions expire after 12 hours or restart. A LAN display cannot retrieve the pairing code. LAN HTTP is for trusted private networks; it does not encrypt traffic. Use localhost on untrusted networks. Safari layout and touch controls are prepared, but real iPad mini 6 testing remains pending.

## Configuration and diagnostics

Environment variables use the `AeroScience__` prefix: `Telemetry` (`Simulated` or `SimConnect`), `SamplingIntervalMs` (250–60000, default 1000), `Port` (default 5080), `DataDirectory`, `EnableLan` (default false), `LanAddress`, and `EnableToolbarBridge` (default true, Windows only).

The engine reports its source, data directory, connection failures and reconnects to its console. Bounded diagnostic files are in `%LOCALAPPDATA%\AeroScience\logs` on Windows (seven daily logs, at most 5 MB each). No sample-by-sample logging or external reporting is enabled. The default database is `%LOCALAPPDATA%\AeroScience\missions.db`.

The updated preview also includes `Collect-Feedback.ps1` (built-in Windows PowerShell) and `Test-SimConnectRuntime.ps1` (PowerShell 7 x64). The former produces a local feedback ZIP without databases, SDK DLLs or credential dumps; the latter checks native DLL architecture/loading/exports on Windows. Neither uploads anything or establishes simulator acceptance.

- Missing live telemetry: check that MSFS has loaded a flight, the runtime is x64, and the connection message in Mission Control. Missing TITLE uses an explicit unknown-aircraft label; a missing AGL value does not block the other telemetry.
- Toolbar channel unavailable: verify MSFS 2024 SU5+ / SDK 1.6.4+ CommBus support, matching runtime exports, and SDK contract compilation. Use the simulator's Coherent/debug tools and the read-only probe. Do not “fix” this by embedding the localhost site.
- Address in use: close the other engine or choose another port. A second engine cannot share the same data directory; this protects an ongoing recording from recovery by a competing process.
- iPad cannot connect: use the PC's LAN address (not 127.0.0.1), check Private-profile firewall rules, same-network membership, guest-network isolation and the selected interface address. Never enter pairing codes into a cloud service.
- Display disconnect: leave the engine running. It records independently and the browser reconnects. New pairing may be needed after an engine restart/session expiry.

## Update, backup and uninstall

Stop the engine, back up its **entire data directory** (including any WAL/SHM files), then replace application/Community package files with the next version. Do not replace the data directory. Schema migrations retain missions and add defaults; an older engine refuses a newer database schema.

To uninstall, stop the engine, delete its extracted application folder and built Community package, and remove any manually created firewall rule. **Keep `%LOCALAPPDATA%\AeroScience`** unless you explicitly choose to delete your mission history. No installer/update/uninstall script removes this directory. All user data stays separate from the installation.
