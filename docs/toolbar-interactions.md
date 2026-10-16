# Native toolbar interaction investigation and Windows retest

**Blocking v0.1.0 issue: unresolved.** Retesting `9a1946d` did not establish normal title-bar dragging or resizing. The user reports that the left edge shows a resize cursor, but dragging there moves the entire window rather than resizing it. Lower content is clipped; scrolling was not confirmed in that latest retest. Synthetic telemetry still reaches the full panel. These are observations, not a diagnosed hit-testing or CSS cause.

The user also confirms that **built-in panels drag and resize normally in the same session**. This narrows the investigation toward AeroScience's integration or the template/configuration path used by this add-on, without establishing a specific cause.

This revision removes Compact VR entirely and supplies a controlled native-window comparison and passive debugger capture. It does **not** claim a new drag/resize correction. The production registration, native host attributes, frame/wrapper geometry and scroll mechanism from `9a1946d` are deliberately unchanged pending native evidence. There is no custom dragging, resizing, wheel interception or replacement window system.

Keep the existing engine at `C:\Windows-Companion-unverified-in-MSFS`, its runtime files, `wwwroot` and personal official `SimConnect.dll`. No replacement companion is required. Mission data remains in `%LOCALAPPDATA%\AeroScience`.

## Findings and unresolved questions

Reviewed the four production toolbar files, the POC, build/package checks, Microsoft's handler/listener declarations, the official debugger instructions and secondary toolbar templates before editing. Sources:

- Official [MSFS 2024 JavaScript guidance](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/JavaScript/JavaScript.htm) and [Coherent GT debugger procedure](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/JavaScript/Coherent_GT_Debugger.htm). The debugger page was accessible for this investigation; initial historical access failures do not describe current access.
- Microsoft's [ResizeHandler/DragDropHandler declarations](https://github.com/microsoft/msfs-avionics-mirror/blob/366be5056166c639a2189e09e5af7143174fd910/src/msfstypes/js/common.d.ts) and [toolbar listener declarations](https://github.com/microsoft/msfs-avionics-mirror/blob/366be5056166c639a2189e09e5af7143174fd910/src/msfstypes/js/services/toolbarpanels.d.ts).
- Secondary [window template](https://github.com/bymaximus/msfs2020-toolbar-window-template/tree/ee0ca555b894cbd94a5f58f50057b3510c61cb4a), [Parallel 42 example](https://github.com/parallel42/msfs-toolbar-interop/tree/4deb0de5e448e1898cd094b86df4fa146a0ab317) and [MSFS 2024 CommBus example](https://github.com/Dutch-FlightSim-Developer/developing-with-simconnect/tree/2c7163804eeca1484727c36653c23133200ec773/part-13/13-4%20-%20CommBus%20from%20Toolbar). These are implementation references, not verification of our installed panel.

The public official references expose handler APIs, not the installed simulator's complete `ingame-ui` implementation. That implementation and actual rendered rectangles are unavailable in this Linux environment. No proprietary template implementation is copied into source or the package.

Expected edge geometry: a left-edge resize changes x and width while keeping the right edge fixed; a top-edge resize changes y and height while keeping the bottom edge fixed. Right/bottom resizing normally changes width/height while keeping left/top fixed. Title-bar dragging changes position with unchanged dimensions. Compare bounds in the same coordinate system and account for native screen/minimum constraints; movement of the left/top boundary alone is not evidence of a wrong drag handler. The [corrected probe retest](validation-results.md#probe-retest-after-abb8982) confirms button registration/visibility, but header dragging still fails and edge behavior needs measurements.

| Investigation question | Evidence and boundary |
| --- | --- |
| Who owns dragging? | The native `ingame-ui` template supplies its outer header. `DragDropHandler` exposes a configurable `dragElement` and `handleByGame`; the actual element bound by this installed template must be inspected. AeroScience's inner AEROSCIENCE app header is not the native title bar. |
| Who owns resizing? | Native `ResizeHandler` exposes a current handle, direction and game-handling flag. Registration requests `resizeDirections="Both"`; the host requests `resize="both"`. Exact generated handle elements, positions and bindings are not established by public declarations or screenshots. |
| Why does the left edge show a resize cursor? | Confirmed user observation. The declarations include separate native cursor APIs. A cursor alone does not establish which handler receives the drag or whether its hit rectangle is correct. |
| Why does dragging that region move the whole panel? | Unresolved. Need event target/path, native call stack and before/after bounds. A resize on the left normally changes both x and width; translation with unchanged width is a different outcome. |
| Are handles positioned and receiving input? | Unresolved until actual generated DOM, hit targets and handlers are captured. Browser fixtures intentionally do not manufacture these handlers. |
| Are layout, clipping, overlap or pointer rules responsible? | Plausible investigation areas only. Compare the minimal panel and inspect computed bounds/styles; do not infer a cause merely from flex declarations. |
| Is a different 2024 template/configuration required? | No reviewed official source establishes that. Current registration/host conventions agree with secondary toolbar examples. Compare with a built-in panel and inspect the installed implementation before replacing the template. |
| Does scrolling work? | Browser overflow/wheel checks pass independently. Actual Coherent wheel routing and the clipped production content remain unverified. |

## Changes in this revision

- Removed the Compact VR header checkbox, layout-switching JavaScript and compact-only CSS. There is one responsive Mission/Payload interface for desktop and VR. No replacement display toggle.
- Preserved SimConnect, CommBus, mission modes/state, database/history, dashboard, production native-window configuration and the existing read-only POC.
- Added `WindowProbe.html`/`WindowProbe.js`, selected only with `Build-Toolbar.ps1 -WindowProbe`. It uses the same panel ID, registration, native dependencies, host attributes and fallback dimensions, with simple static content and a fixed 160 px scroll box. It omits AeroScience application CSS, flex wrapper overrides, mission code and CommBus. The first version accidentally omitted the button script/template imports; the user's debugger confirmed an unregistered button component, so the probe now includes both imports in the full panel's order. The corrected probe needs a fresh SDK build/restart and readiness/control retest. The [first Windows probe test](validation-results.md#windows-native-window-probe-build-and-first-simulator-test) confirms loading and user-reported scrolling/movement for the earlier version, not correct resizing or complete native controls; it is not a known-working baseline.
- Added `scripts/debug/capture-toolbar-window.js`, pasted manually into the selected Coherent console. Neither production nor probe loads it automatically. It passively records up to 120 snapshots/input/error entries, actual element bounds/styles, event paths and hit samples. It does not change styles, prevent input, replace handlers, record mission values/CommBus payloads, copy native source or upload anything. Stop it before performance measurements.
- Updated tests and documentation. Release acceptance now requires `vr_performance` evidence instead of the obsolete `vr_quest3` interaction record. The example remains pending; an older record must receive new measured performance evidence. Diagnostic receipts, including `toolbar-window-probe`, cannot pass the full-release gate.

## Get the source and build with the official SDK

Close MSFS before replacing Community files. Back up the current installed `aeroscience-toolbar` **outside** all Community folders, retaining its build logs/receipt. Never delete mission data. A clean Git checkout can use `git pull --ff-only origin main`. For a source-ZIP installation, keep the old folder and use a fresh destination:

1. Open **x64 Native Tools Command Prompt**. Type `pwsh -NoProfile` and press Enter. If `pwsh` is not found there, use `"%ProgramFiles%\PowerShell\7\pwsh.exe" -NoProfile` from that Command Prompt.
2. In the resulting PowerShell window:

   ```powershell
   $archive = Join-Path $env:USERPROFILE 'Downloads\AeroScience-native-window-source.zip'
   Invoke-WebRequest -Uri 'https://github.com/JustSeanC/AeroScience/archive/refs/heads/main.zip' -OutFile $archive
   $destination = 'C:\Dev\AeroScience-NativeWindow'
   if (Test-Path -LiteralPath $destination) { throw 'Choose a new empty destination before extracting.' }
   Unblock-File -LiteralPath $archive
   Expand-Archive -LiteralPath $archive -DestinationPath $destination
   Set-Location 'C:\Dev\AeroScience-NativeWindow\AeroScience-main'
   $env:Path = "$PSHOME;$env:Path"
   ```

   This is source, not an executable companion or precompiled Community add-on. The PATH line makes the current PowerShell 7 available to the child `pwsh` calls without changing Windows settings.

3. Build the comparison panel first:

   ```powershell
   pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot 'C:\MSFS 2024 SDK' -WindowProbe
   ```

4. Build the full panel from the same source:

   ```powershell
   pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot 'C:\MSFS 2024 SDK'
   ```

Both run the preserved installed-header C++ contract, official `fspackagetool.exe`, structural validation and SHA-256 receipt generation. Expect Microsoft's `Validation SUCCESS`, our structural validation and `SDK-built output`. If a step fails, retain the logs and stop; no successful receipt should be inferred. SDK-generated manifests/layouts must not be hand-edited.

The two isolated outputs are:

```text
C:\Dev\AeroScience-NativeWindow\AeroScience-main\artifacts\toolbar-window-probe\output\Packages\aeroscience-toolbar
C:\Dev\AeroScience-NativeWindow\AeroScience-main\artifacts\toolbar-full\output\Packages\aeroscience-toolbar
```

Use the actual path printed by each build if the SDK differs. Both packages register the **same** panel ID and package name: install exactly one at a time. `-ProofOfConcept` still builds the unchanged CommBus diagnostic; it cannot be combined with `-WindowProbe`.

## Install and compare on desktop

1. While MSFS is closed, replace the entire old installed `aeroscience-toolbar` folder with the **probe** output in the same configured Community folder that previously loaded AeroScience. Do not copy `PackageSources` or merge old/new files. The installed folder must directly contain `manifest.json`, `layout.json`, `InGamePanels` and `html_ui`. Use Developer Mode → Tools → Virtual File System → Packages Folders → Open Community Folder to locate it if needed. Do not install another copy in Community2024; its compatibility remains unverified.
2. Restart MSFS and load a desktop flight. The user already confirmed built-in panels move/resize normally; briefly repeat that comparison in the new session to retain a controlled baseline.
3. Open AeroScience. It must say **NATIVE WINDOW PROBE — NO MISSION CONTROLS**. The engine is unnecessary for this comparison. Try the outer title bar, each supported edge/corner, especially the left edge, then scroll inside the bordered box to END. Record failures and obtain the capture below. Even a collapsed/incorrectly sized probe is useful evidence; do not adjust its CSS before recording that result.
4. Close MSFS, replace the probe with the **full** output, and restart/load the same flight. The full panel has Mission/Payload only, with no Compact VR control. If old assets appear, verify the copied output and full simulator restart before testing.
5. For the full panel, keep the existing companion console running:

   ```powershell
   Set-Location 'C:\Windows-Companion-unverified-in-MSFS'
   .\AeroScience.Engine.exe --AeroScience:Telemetry=Simulated --AeroScience:EnableToolbarBridge=true --AeroScience:EnableLan=false
   ```

   Run only one engine. The standalone `Launch-Simulated.cmd` disables the bridge, so use the command above for toolbar testing. Open `http://127.0.0.1:5080` in the PC browser. **SIMULATED TEST DATA** is expected; this is not a live-aircraft telemetry test.
6. Repeat the same native interactions and capture on the full panel. Complete the checklist below. If the probe works and the full panel fails, compare the captured wrapper/handle geometry and hit paths. If both fail but built-in panels work, investigate shared registration/native setup and template errors. If built-in panels also fail, establish the broader simulator input behavior before attributing it to AeroScience. These outcomes narrow the investigation; none alone identifies a root cause.

## Capture the actual native targets and geometry

1. Keep the failing AeroScience panel open. Run `C:\MSFS 2024 SDK\Tools\CoherentGT Debugger\Debugger.exe`. Click **Go** with `http://127.0.0.1:19999` as documented by Microsoft. Select the view containing AeroScience/its HTML page; do not assume a VCockpit entry is the toolbar. In its console, `document.getElementById('aeroscience-frame')` must find the frame. If not, choose another UI view. Record a debugger connection/view-discovery failure as **Blocked**.
2. Check **Ignore Cache** in Network, per the official guide. Copy the entire local `scripts\debug\capture-toolbar-window.js` file into that view's JavaScript console and run it. This code goes into the debugger, **not PowerShell**.
3. Return to the simulator. Attempt one title-bar drag and one left-edge resize. Hover/cursor appearance must also be recorded manually; the native cursor can differ from CSS `cursor`. Attempt one corner resize and one wheel scroll in the content region. Do not type private mission names during evidence collection.
4. Back in the same debugger console, run:

   ```javascript
   AeroScienceWindowDebug.snapshot('after-title-left-corner-wheel');
   AeroScienceWindowDebug.stop();
   console.log(AeroScienceWindowDebug.report());
   ```

   Copy the resulting JSON text to `window-probe-capture.json` or `toolbar-full-capture.json` on your PC. Review it before sharing. The capture has no automatic export/upload. Its ring buffer is intentionally bounded; capture a short reproduction and start a new capture for another scenario. Reloading the view also removes the observer.
5. In Elements, inspect the **actual generated** `ingame-ui-header`, frame wrappers and resize-looking nodes recorded in the JSON. Check Event Listeners for the title bar and left edge if that debugger version provides it. In Sources, search the installed `common.js`/`ingameUi` scripts for `DragDropHandler`, `ResizeHandler`, `startDrag` and `startResize`. Break at the relevant method or mouse-down event breakpoint if available, reproduce, and record the native file/line, call stack, target/currentTarget, bound drag element/current handle and game-handling flag. Names may differ by installed build: inspect rather than guess. Resume promptly; stopping JavaScript delays panel polling. Do not redistribute the proprietary script implementation.
6. Compare before/after x/y/width/height. Left-edge resizing can change x **and** width; a whole-window move preserves width/height. Compare capture event paths and `elementFromPoint` hits with the visible title bar/handles, including overlapping nodes and inherited pointer rules. A passive capture cannot reveal every native/game-side binding; the debugger call stack is needed where routing remains ambiguous.
7. For scrolling, compare `clientHeight`, `scrollHeight` and `scrollTop` before/after real wheel input. If content overflows but the wheel does not change `scrollTop`, test a temporary programmatic scroll in the debugger to separate a scrollable box from wheel delivery. If there is no overflow despite visible clipping, inspect wrapper bounds. Record the actual result; do not treat programmatic scrolling as an input pass or change production styles without evidence.

For a repeat failure, send the two captures, native console errors/call-stack file/line, built-in-panel comparison, source revision, `sdk-build.json`, `sdk-contract.log`, `build.log`, game/SDK installer versions, resolution/UI scaling, aircraft, actual installed path and exact steps. Then choose a targeted correction from that evidence before another build. SDK installer version is not the simulator's reported game version.

## Desktop acceptance record

Use **Passed**, **Failed**, **Blocked** or **Not yet tested**, with evidence and date. All new-build rows begin **Not yet tested**. The reported failures below concern `9a1946d`, not this source revision.

| Test | Latest reported `9a1946d` result | New full build |
| --- | --- | --- |
| Open panel and receive changing synthetic metrics | Passed | Not yet tested |
| Drag the outer simulator title bar | Failed | Not yet tested |
| Resize supported edges/corners in both directions | Failed | Not yet tested |
| Left-edge drag changes dimensions instead of only translating window | Failed: resize cursor, whole-window movement | Not yet tested |
| Scroll clipped content; reach mode/name/start/end controls with header/nav usable | Not confirmed in latest retest; earlier failure | Not yet tested |
| Mission → Payload → Mission; controls remain clickable | Not yet tested | Not yet tested |
| Start Universal; compare mission ID/mode/samples with browser during moving/resizing | Earlier full build: active Universal display only | Not yet tested |
| End from either display; confirm history/mode/telemetry; repeat Realistic | Not yet tested | Not yet tested |
| Close/reopen panel; recording and samples continue; mission unchanged | Not yet tested | Not yet tested |
| No new rendering/input regressions; native header controls work | Not yet tested | Not yet tested |

Record probe title-bar, left-edge, other-edge/corner and wheel results separately, along with the built-in-panel comparison. The probe contains no telemetry or mission controls. Desktop simulator testing is the primary functional acceptance; browser layout tests and SDK compilation cannot fill these rows.

## VR performance only

There is one responsive interface. No separate Quest 3 interaction, controller, scrolling, resizing or layout-toggle matrix is required. After desktop functionality works, use a normally working VR headset session and keep aircraft, location/weather, headset connection/OpenXR runtime, graphics/resolution and traffic settings constant.

Compare equal observation intervals (for example 60 seconds each), repeating to distinguish ordinary simulator variation:

1. Companion stopped, AeroScience panel closed.
2. Companion running/recording the same chosen telemetry source, panel closed.
3. Companion running/recording, panel open. Close/open it again and watch for spikes.

Record available simulator FPS and frame times (median/high-percentile if the measurement tool supports them), stutters, companion CPU/memory, simulator resource usage and any available Coherent view cost. State the tool, interval, settings and whether measurements include debugger overhead; close the capture/debugger during the comparison. No numerical pass threshold has been measured or invented. Report noticeable degradation and raw measurements before marking `vr_performance` passed. Results remain **Not yet tested**.

## Automated evidence and outstanding work

Current executed results are in [validation-results.md](validation-results.md). Browser fixtures test application layout/scrolling, navigation, modes/protocol and capture utility behavior with mocked SDK services. They supply frame bounds; they do not implement native dragging/resizing or establish Coherent input success.

The official Windows SDK and MSFS are absent from this Linux cloud. SDK compilation and native input/call-stack inspection are **Blocked here**. The user subsequently [built and loaded the probe on Windows](validation-results.md#windows-native-window-probe-build-and-first-simulator-test); its scrolling/movement reports do not establish normal resizing, full-toolbar acceptance or VR performance. New full-build compilation and those remaining native checks are unverified. No cloud-generated precompiled package or successful native correction is claimed.

Keep each variant's build logs/receipt under `artifacts\toolbar-full` or `artifacts\toolbar-window-probe`. The existing `Collect-Feedback.ps1 -BuildLogDirectory 'YOUR SOURCE\artifacts\toolbar-full'` collects selected build/runtime logs but does not automatically include the manually saved debugger JSON; share those separately after review. Engine logs remain in `%LOCALAPPDATA%\AeroScience\logs`.

Native window interaction remains a release blocker. Live aircraft telemetry/compatibility, full PC mission persistence/lifecycle, reconnect/recovery, clean distribution/runtime licensing, Community2024 and physical iPad testing also remain outstanding. No new science, replay or release-automation work is included.
