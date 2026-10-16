# v0.1.0 toolbar interaction changes and PC retest

This revision changes toolbar source only. Keep using the existing companion at `C:\Windows-Companion-unverified-in-MSFS`, including its official native `SimConnect.dll`, runtime files and `wwwroot`. No replacement engine is required.

The user built and opened the **previous full panel** in MSFS, received simulated telemetry, and showed an active Universal mission with a timer/sample count. They reported that moving, resizing and scrolling did not work. This supersedes the earlier diagnostic-only milestone. This new revision has automated evidence only; its SDK build and actual desktop/Quest 3 interactions are **not yet tested**.

## Investigation and limits of the diagnosis

Reviewed before editing:

- Official [MSFS 2024 JavaScript/Coherent guidance](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/JavaScript/JavaScript.htm), [SDK contents](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_Contents.htm), and [sample catalog](https://docs.flightsimulator.com/msfs2024/html/7_Samples_Tutorials/Samples_And_Tutorials.htm).
- Microsoft's [native ResizeHandler/DragDropHandler declarations](https://github.com/microsoft/msfs-avionics-mirror/blob/366be5056166c639a2189e09e5af7143174fd910/src/msfstypes/js/common.d.ts) and [toolbar listener declarations](https://github.com/microsoft/msfs-avionics-mirror/blob/366be5056166c639a2189e09e5af7143174fd910/src/msfstypes/js/services/toolbarpanels.d.ts).
- Secondary toolbar implementation references: the [MSFS 2024 CommBus panel](https://github.com/Dutch-FlightSim-Developer/developing-with-simconnect/tree/2c7163804eeca1484727c36653c23133200ec773/part-13/13-4%20-%20CommBus%20from%20Toolbar), [window template](https://github.com/bymaximus/msfs2020-toolbar-window-template/tree/ee0ca555b894cbd94a5f58f50057b3510c61cb4a), and [Parallel 42 interop example](https://github.com/parallel42/msfs-toolbar-interop/tree/4deb0de5e448e1898cd094b86df4fa146a0ab317).

The public official pages explain the Coherent environment but do not fully specify the legacy `ingame-ui` template's movement and wheel routing. The external examples establish the attribute form/native-template conventions, not proof of this revision's simulator behavior. No simulator-owned template is copied into the package. Coherent vendor documentation/developer-support search could not be fetched from this environment; no unavailable source is presented as reviewed.

| Issue | Source finding and change | Remaining uncertainty |
| --- | --- | --- |
| Dragging | Keep the existing simulator-owned title bar and native handlers. Express registration size/position settings as definition attributes, as in the toolbar references; remove the full panel's fixed pixel geometry. No custom drag listeners, pointer capture or position-saving code added. | The screenshot does not identify the exact native drag failure. Attribute/layout corrections are a candidate fix; only an MSFS retest can confirm movement. |
| Resizing | `resizeDirections` and size hints were child elements rather than definition attributes. Supply `resizeDirections="Both"` on the definition; retain `resize="both"` on `ingame-ui`. Use a viewport-relative fallback, let native inline bounds win, and keep the outer frame/resize chrome unclipped. | Current SDK acceptance and actual edge/corner input require recompilation/retest. Correct package generation alone does not prove resize input. |
| Scrolling | The former wrappers each forced `height:100%`, and the whole app also had full height with overflow. Replace that unbounded chain with shrinkable flex wrappers (`min-height:0`) and a dedicated, bounded `#as-scroll` content region. | Browser wheel scrolling passes; MSFS wheel/touch/controller routing remains unverified. No custom wheel interception added. |
| Compact VR | It was a button next to Mission/Payload, despite changing only a CSS class. Move it to the app header as a labeled checkbox with visible Off/On state. | Actual simulator checkbox/VR input must be tested. |

The native header is distinct from AeroScience's inner app header. Move the window using the **outer simulator title bar**. The content below the status line scrolls; app navigation and the Compact VR switch remain outside that scroll region. The layout switch changes only presentation, without reloading the page, changing the selected page or sending mission mutations.

The full frame has a 320×360 px CSS/HTML fallback minimum plus SDK registration bounds. Native constraints and screen/VR scaling can impose a larger effective minimum. The source uses 42vw/55vh as initial fallback dimensions; native inline resizing takes precedence. The diagnostic HTML/JS is unchanged and retains its previous CSS geometry. Its shared registration now also uses the attribute form.

Changed source files: `msfs-toolbar/PackageSources/InGamePanels/aeroscience_panel.xml` and `msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/AeroScience.html`, `AeroScience.css`, `AeroScience.js`. Regression support is in `tests/web/helpers/toolbar.mjs`, `toolbar-layout.spec.js` and the existing `toolbar-contract.spec.js`. The changelog, README, handoff, verification and evidence documents were updated. Companion code, shared protocol, database code, SDK contract C++, build scripts and package validators were not modified.

## Automated evidence

- .NET: **23 passed**, `tests/AeroScience.Tests/TestResults/toolbar-interactions.trx`.
- Complete browser suite: **9 passed**, including the unchanged dashboard workflows and toolbar protocol regression. The five toolbar checks also passed after the narrow-header spacing adjustment.
- Python packaging/helper discovery with PowerShell available: **18 passed**. SDK ABI checks, three/four-part version handling, case-insensitive layout matching, file/receipt validation and compiler-failure behavior are preserved.
- Toolbar JavaScript syntax and all toolbar XML parsing passed. These are syntax checks, not an official SDK build.
- Browser tests cover 320×360, 640×720 and 900×900 supplied bounds; standard/compact scrolling and accessible mission controls; page/state retention across layout changes; ongoing mocked replies; and hidden/disconnected/reconnected panel state. They do not implement or prove native movement/resizing, Coherent wheel routing or VR input.
- Browser fixture screenshots are retained under ignored `artifacts/toolbar-interactions/`. The fixture labels its native-header placeholder; these images are not MSFS captures.

The Windows SDK package build is **Blocked in this Linux cloud environment**: there is no official Windows package tool/simulator here. There is no newly generated Community package, SPB, layout, manifest or successful SDK receipt for this revision. Build those on the configured gaming PC below. Earlier successful SDK logs apply to the previous source revision.

## Get current source and build on Windows

Stop the companion with Ctrl+C and close MSFS before replacing packages. Preserve the current installed `aeroscience-toolbar` outside Community, and retain its existing build logs/receipt for comparison. Mission data remains in `%LOCALAPPDATA%\AeroScience`; do not delete it.

If `C:\Dev\AeroScience` is a clean Git checkout, update it with `git pull --ff-only origin main`. If it was extracted from a source ZIP, the following fresh-source route preserves it:

1. Open **x64 Native Tools Command Prompt**, then run `pwsh -NoProfile` as before.
2. In PowerShell, use an empty/new destination:

   ```powershell
   $archive = Join-Path $env:USERPROFILE 'Downloads\AeroScience-toolbar-source.zip'
   Invoke-WebRequest -Uri 'https://github.com/JustSeanC/AeroScience/archive/refs/heads/main.zip' -OutFile $archive
   $destination = 'C:\Dev\AeroScience-ToolbarFix'
   if (Test-Path -LiteralPath $destination) { throw 'Choose a new empty destination before extracting.' }
   Expand-Archive -LiteralPath $archive -DestinationPath $destination
   Set-Location 'C:\Dev\AeroScience-ToolbarFix\AeroScience-main'
   ```

   This ZIP contains **source**, not a built Community add-on or replacement engine. If Windows shows an Unblock checkbox in the ZIP's Properties, unblock this trusted repository ZIP before extracting. Use your existing Git source folder instead when you updated that checkout.

3. Build the full panel with the same SDK/compiler/Python tools already used successfully:

   ```powershell
   pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot 'C:\MSFS 2024 SDK'
   ```

   Do not add `-ProofOfConcept` for this full-panel retest. The script performs the preserved header contract, official package build, structural validation and receipt hashing. Expect Microsoft's `Validation SUCCESS`, our structural pass and `SDK-built output`. The companion executable is not rebuilt.

4. Copy the **actual SDK-built** `aeroscience-toolbar` folder printed by the script into the same configured Community location where the previous full panel opened. Replace the entire old folder, with only one variant installed. For the fresh extraction above, the expected source is:

   ```text
   C:\Dev\AeroScience-ToolbarFix\AeroScience-main\artifacts\toolbar-full\output\Packages\aeroscience-toolbar
   ```

   The destination must contain `aeroscience-toolbar\manifest.json`, `layout.json`, `InGamePanels` and `html_ui` directly. Do not install `PackageSources`. No tool/header paths or companion protocol settings have changed. Community2024 compatibility remains a separate unverified check.

5. Restart MSFS, load a flight, and launch the existing engine for synthetic testing:

   ```powershell
   Set-Location 'C:\Windows-Companion-unverified-in-MSFS'
   .\AeroScience.Engine.exe --AeroScience:Telemetry=Simulated --AeroScience:EnableToolbarBridge=true --AeroScience:EnableLan=false
   ```

   Keep its console running. Open AeroScience in the simulator and `http://127.0.0.1:5080` in the PC browser. The panel should say **SIMULATED TEST DATA** and have exactly two navigation buttons plus a **Compact VR: Off/On checkbox in the app header**. A third Compact VR navigation button means the old assets are still loaded; verify the copied SDK output and fully restart MSFS. Start with synthetic data so the interaction retest does not imply a real-aircraft telemetry pass.

## Desktop acceptance record

For each new result use exactly **Passed**, **Failed**, **Blocked** or **Not yet tested**. Fill in the source revision, MSFS/SDK versions, screen resolution/UI scaling, aircraft, date/time and actual install path. The baseline column is the user's report about the previous build, not this revision.

| Test and reproduction steps | Previous full-panel evidence | This revision | Notes/evidence |
| --- | --- | --- | --- |
| Open the full panel; confirm changing synthetic metrics/source | Passed | Not yet tested | |
| Drag the outer simulator title bar to a new position | Failed | Not yet tested | |
| Drag supported window edges/corner larger and smaller; watch content follow the bounds | Failed | Not yet tested | |
| Reduce height; hover content below status; scroll down/up with mouse wheel and reach every mission control | Failed | Not yet tested | |
| Navigate Mission → Payload → Mission; verify controls remain clickable | Not yet tested | Not yet tested | |
| On Payload, toggle Compact VR On/Off; confirm the page stays Payload and the switch state is clear | Not yet tested | Not yet tested | |
| Start an expendable Universal mission; move/resize/scroll/toggle layouts; verify mission ID and increasing sample count in toolbar and browser | Active Universal display only | Not yet tested | |
| End from one display and confirm the other; repeat with Realistic, retaining its future-features explanation | Not yet tested | Not yet tested | |
| Close/reopen only the panel; confirm the same active mission and continuing samples | Not yet tested | Not yet tested | |
| Confirm recorded history/mode/telemetry and browser dashboard still work | Not yet tested on this PC | Not yet tested | |

Any failure: record exact steps, expected/actual behavior, standard/compact layout, window size, current page, last working action, relevant logs and a screenshot. End the expendable test mission when finished. This record does not establish long-session/reconnection or live aircraft compatibility.

## Separate Quest 3 acceptance record

First establish a normal working PCVR session. Record Link/Air Link/Virtual Desktop, active OpenXR runtime, simulator/VR scaling and controller/mouse used. Repeat in standard and compact layouts. Desktop results do not fill in this table.

| Test | This revision | Notes/evidence |
| --- | --- | --- |
| Open/position the native panel in VR | Not yet tested | |
| Resize using the simulator-supported VR interaction; record any limitation | Not yet tested | |
| Scroll overflowing content with the supported controller interaction and with the mouse | Not yet tested | |
| Operate navigation, mode/name fields and mission buttons with controller/mouse | Not yet tested | |
| Read essential telemetry, source/mode and switch state at normal cockpit distance | Not yet tested | |
| Toggle Compact VR On/Off without changing page, mission ID or recording | Not yet tested | |
| Reach all critical mission controls in a smaller window | Not yet tested | |
| Transition desktop → VR → desktop and close/reopen the panel without losing the active mission | Not yet tested | |

If a particular interaction is not supported by the installed native VR window system, mark that test **Blocked**, describe the limitation and record the supported alternative actually observed. Do not add competing custom drag/controller handlers without new evidence.

## Logs and remaining limits

Keep `sdk-contract.log`, `build.log` and `sdk-build.json` from this revision's `artifacts\toolbar-full` beside its generated package. The Windows script generates them; none has been fabricated in the cloud. Preserve console output and relevant `%LOCALAPPDATA%\AeroScience\logs` entries. From the engine directory, the existing `Collect-Feedback.ps1 -BuildLogDirectory 'YOUR UPDATED SOURCE\artifacts\toolbar-full'` collects selected reports for review; it uploads nothing.

If native movement/resize/wheel still fails, use the official [Coherent GT debugger procedure](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/JavaScript/Coherent_GT_Debugger.htm) to capture the AeroScience view's console errors and inspect `#aeroscience-frame`, its generated native wrappers/title bar/resize handles and `#as-scroll`. Record actual computed sizes/overflow and inline styles before/after attempted interaction. Browser fixture behavior cannot diagnose the installed native template's hit testing.

No new sensors, mapping, mission types, Realistic restrictions, replay or release automation were added. Real aircraft telemetry, aircraft compatibility, full mission mutation/persistence on the PC, reconnect/recovery, clean distribution and physical iPad acceptance remain separate outstanding work.
