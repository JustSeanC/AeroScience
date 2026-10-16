# Native toolbar interaction investigation and Windows retest

**Native bootstrap corrected in source; clean-start acceptance pending.** The probe's native drag/resize handlers were absent. Manually running its installed SDK storage-ready initializer created both handlers, supplied full-view geometry and restored moving/resizing in the user's live test. The full and probe pages now load the missing `/JS/dataStorage.js` dependency. Automatic initialization in a freshly built/restarted package and complete full-panel interaction remain unverified.

Earlier `9a1946d` retesting failed normal title-bar dragging/resizing, with a resize cursor but apparent whole-window movement at the left edge. Lower production content was clipped and scrolling unconfirmed; synthetic telemetry still arrived. Keep those historical results distinct from the later manually initialized probe and the upcoming corrected-package test.

The user also confirms that **built-in panels drag and resize normally in the same session**. This narrows the investigation toward AeroScience's integration or the template/configuration path used by this add-on, without establishing a specific cause.

This revision removes Compact VR entirely and supplies a controlled native-window comparison and passive debugger capture. The only production behavior change in the current bootstrap correction is loading the simulator's storage dependency before native templates. Registration, host attributes, fallback geometry, content scrolling and engine behavior are unchanged. The SDK supplies its handlers and full-view inline bounds; no manual initializer invocation, custom dragging/resizing or wheel interception is shipped.

Keep the existing engine at `C:\Windows-Companion-unverified-in-MSFS`, its runtime files, `wwwroot` and personal official `SimConnect.dll`. No replacement companion is required. Mission data remains in `%LOCALAPPDATA%\AeroScience`.

## Findings and unresolved questions

Reviewed the four production toolbar files, the POC, build/package checks, Microsoft's handler/listener declarations, the official debugger instructions and secondary toolbar templates before editing. Sources:

- Official [MSFS 2024 JavaScript guidance](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/JavaScript/JavaScript.htm) and [Coherent GT debugger procedure](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/JavaScript/Coherent_GT_Debugger.htm). The debugger page was accessible for this investigation; initial historical access failures do not describe current access.
- Microsoft's [ResizeHandler/DragDropHandler declarations](https://github.com/microsoft/msfs-avionics-mirror/blob/366be5056166c639a2189e09e5af7143174fd910/src/msfstypes/js/common.d.ts) and [toolbar listener declarations](https://github.com/microsoft/msfs-avionics-mirror/blob/366be5056166c639a2189e09e5af7143174fd910/src/msfstypes/js/services/toolbarpanels.d.ts).
- Secondary [window template](https://github.com/bymaximus/msfs2020-toolbar-window-template/tree/ee0ca555b894cbd94a5f58f50057b3510c61cb4a), [Parallel 42 example](https://github.com/parallel42/msfs-toolbar-interop/tree/4deb0de5e448e1898cd094b86df4fa146a0ab317) and [MSFS 2024 CommBus example](https://github.com/Dutch-FlightSim-Developer/developing-with-simconnect/tree/2c7163804eeca1484727c36653c23133200ec773/part-13/13-4%20-%20CommBus%20from%20Toolbar). These are implementation references, not verification of our installed panel.

The public official references expose handler APIs, not the installed simulator's complete `ingame-ui` implementation. The simulator and its live rectangles are unavailable in this Linux environment; later user-supplied captures provide measured rectangles and selected initialization methods. No proprietary template implementation is copied into source or the package.

Expected edge geometry: a left-edge resize changes x and width while keeping the right edge fixed; a top-edge resize changes y and height while keeping the bottom edge fixed. Right/bottom resizing normally changes width/height while keeping left/top fixed. Title-bar dragging changes position with unchanged dimensions. Compare bounds in the same coordinate system and account for native screen/minimum constraints; movement of the left/top boundary alone is not evidence of a wrong drag handler. The [corrected probe retest](validation-results.md#probe-retest-after-abb8982) confirms button registration/visibility, but header dragging still fails and edge behavior needs measurements.

The [first exported geometry capture](validation-results.md#first-exported-native-geometryinput-capture) establishes that the frame fills only 42 percent of the document width and remains 360 px high inside a 432 px document. Native resize changes the document width, while the source's viewport-relative frame sizing scales it again. Native inline width/height do not override those declarations in the captured template. Header presses reach the generated header wrapper. Before the proposed reversible sizing experiment or another build, the user chose to compare working panels and inspect native initialization.

The [working VSR comparison](validation-results.md#working-weather-and-vsr-window-comparisons) and [Navigraph/SDK inspection](validation-results.md#navigraph-comparison-and-installed-sdk-initialization-inspection) show full-view frames with `content-fit="true"` and native `fittedContent`/`draggable` classes. The installed attribute callback confirms that content-fit changes `fittedContent`; it does not explain `draggable`. The installed frame also submits its measured bounds to the simulator. Do not force the missing class or assume the content-fit attribute alone enables dragging.

The [next constructor/callback capture](validation-results.md#native-dragresize-initializer-located) locates both native handler constructors and full-view inline sizing in `onStorageReady`. Both handlers are absent from the failed probe even though game handling is enabled. The reference template also imports `/JS/dataStorage.js`, which our two pages omit. Next, use a guarded, once-only call to the existing native initializer in the probe and retest, recording exceptions and before/after handler presence, classes and bounds. Reload the view to discard this temporary diagnostic state. This is an explicit initialization experiment, distinct from the passive capture; success and the eventual bootstrap correction remain unverified. Do not call the initializer again if either handler exists or dispatch a document-wide readiness event.

The [manual initialization result](validation-results.md#manual-native-initialization-result) confirms that this callback created both native handlers, enabled dragging and expanded the frame from 907×360 to the full 2160×432 viewport, with no caught exception. The user then explicitly confirmed moving and resizing work. This establishes a probe interaction pass after manual initialization; it does not establish automatic bootstrap in the rebuilt package or exhaustive edge/corner/control behavior. The unidentified failed-resource line still needs its exact URL/status. See the [source correction and retest boundary](validation-results.md#probe-movementresize-pass-and-storage-bootstrap-correction).

| Investigation question | Evidence and boundary |
| --- | --- |
| Who owns dragging? | The installed SDK initializer binds its native `DragDropHandler` to the generated `headerElement` with game handling enabled. Manual initialization restored probe movement. AeroScience's inner app header is separate from the native title bar. |
| Who owns resizing? | The installed SDK initializer creates `ResizeHandler` with game handling enabled. Manual initialization restored probe resizing. Registration requests `resizeDirections="Both"`; the host requests `resize="both"`. Exact individual edge/corner acceptance remains pending. |
| Why does the left edge show a resize cursor? | Confirmed user observation. The declarations include separate native cursor APIs. A cursor alone does not establish which handler receives the drag or whether its hit rectangle is correct. |
| Why does dragging that region move the whole panel? | Unresolved. Need event target/path, native call stack and before/after bounds. A resize on the left normally changes both x and width; translation with unchanged width is a different outcome. |
| Are handles positioned and receiving input? | Unresolved until actual generated DOM, hit targets and handlers are captured. Browser fixtures intentionally do not manufacture these handlers. |
| Are layout, clipping, overlap or pointer rules responsible? | Plausible investigation areas only. Compare the minimal panel and inspect computed bounds/styles; do not infer a cause merely from flex declarations. |
| Is a different 2024 template/configuration required? | No reviewed official source establishes that. Current registration/host conventions agree with secondary toolbar examples. Compare with a built-in panel and inspect the installed implementation before replacing the template. |
| Does scrolling work? | Browser overflow/wheel checks pass independently. Actual Coherent wheel routing and the clipped production content remain unverified. |

## Changes in this revision

- Added `/JS/dataStorage.js` after common.js in both full/probe pages, before native template imports. The native bootstrap regression now requires that dependency exactly once and in the correct order. Its absence reproduces a test failure; adding it passes. This restores the intended SDK readiness path; actual automatic initialization still needs a fresh simulator test.
- Removed the Compact VR header checkbox, layout-switching JavaScript and compact-only CSS. There is one responsive Mission/Payload interface for desktop and VR. No replacement display toggle.
- Preserved SimConnect, CommBus, mission modes/state, database/history, dashboard, production registration/layout configuration and the existing read-only POC.
- Added `WindowProbe.html`/`WindowProbe.js`, selected only with `Build-Toolbar.ps1 -WindowProbe`. It uses the same panel ID, registration, native dependencies, host attributes and fallback dimensions, with simple static content and a fixed 160 px scroll box. It omits AeroScience application CSS, flex wrapper overrides, mission code and CommBus. The first version accidentally omitted the button script/template imports; the user's debugger confirmed an unregistered button component, so the probe now includes both imports in the full panel's order. The corrected probe needs a fresh SDK build/restart and readiness/control retest. The [first Windows probe test](validation-results.md#windows-native-window-probe-build-and-first-simulator-test) confirms loading and user-reported scrolling/movement for the earlier version, not correct resizing or complete native controls; it is not a known-working baseline.
- Added `scripts/debug/capture-toolbar-window.js`, pasted manually into the selected Coherent console. Neither production nor probe loads it automatically. It passively records up to 120 snapshots/input/error entries, actual element bounds/styles, event paths and hit samples. It does not change styles, prevent input, replace handlers, record mission values/CommBus payloads, copy native source or upload anything. Stop it before performance measurements.
- Updated tests and documentation. Release acceptance now requires `vr_performance` evidence instead of the obsolete `vr_quest3` interaction record. The example remains pending; an older record must receive new measured performance evidence. Diagnostic receipts, including `toolbar-window-probe`, cannot pass the full-release gate.

## Verify automatic initialization after rebuilding

The successful manual initializer call is temporary diagnostic state. Build/install the corrected full package using the instructions below, restart MSFS completely, and test moving/resizing **before running any initializer in the debugger**. The existing companion remains unchanged. A fresh probe build is also available if a minimal comparison is needed; only install one variant.

In the selected new AeroScience view's Coherent Console, this read-only check can confirm automatic setup:

```javascript
(function () {
  const frame = document.getElementById('aeroscience-frame');
  if (!frame) throw new Error('Select the AeroScience view first.');
  const rect = frame.getBoundingClientRect();
  return {
    page: location.href,
    classes: frame.getAttribute('class'),
    dragHandlerPresent: !!frame.dragDropHandler,
    dragEnabled: frame.dragDropHandler ? frame.dragDropHandler.canBeDragged : null,
    resizeHandlerPresent: !!frame.resizeHandler,
    viewport: { width: innerWidth, height: innerHeight },
    frame: { width: rect.width, height: rect.height }
  };
})()
```

Expected: both handlers present, dragging enabled, and the open attached game-managed frame filling its view. Confirm real title dragging and each edge/corner separately; metadata alone is insufficient. Test scrolling/navigation/mission controls on the full interface and close/reopen the panel, then restart MSFS again to check repeatability. If automatic setup fails, preserve the failure and exact console resource URL/status rather than manually initializing to make the acceptance test pass. A `.map` error concerns debugging metadata; a failed runtime script/template needs its own diagnosis.

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

3. Build the full panel for the automatic-startup retest:

   ```powershell
   pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot 'C:\MSFS 2024 SDK'
   ```

4. If a minimal comparison is needed, also build the probe from the same source:

   ```powershell
   pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\build\Build-Toolbar.ps1 -SdkRoot 'C:\MSFS 2024 SDK' -WindowProbe
   ```

Both run the preserved installed-header C++ contract, official `fspackagetool.exe`, structural validation and SHA-256 receipt generation. Expect Microsoft's `Validation SUCCESS`, our structural validation and `SDK-built output`. If a step fails, retain the logs and stop; no successful receipt should be inferred. SDK-generated manifests/layouts must not be hand-edited.

The two isolated outputs are:

```text
C:\Dev\AeroScience-NativeWindow\AeroScience-main\artifacts\toolbar-window-probe\output\Packages\aeroscience-toolbar
C:\Dev\AeroScience-NativeWindow\AeroScience-main\artifacts\toolbar-full\output\Packages\aeroscience-toolbar
```

Use the actual path printed by each build if the SDK differs. Both packages register the **same** panel ID and package name: install exactly one at a time. `-ProofOfConcept` still builds the unchanged CommBus diagnostic; it cannot be combined with `-WindowProbe`.

## Install and compare on desktop

1. While MSFS is closed, replace the entire old installed `aeroscience-toolbar` folder with the chosen output in the same configured Community folder that previously loaded AeroScience. Start with the **full** output for the current automatic-startup retest; use the probe separately if comparison is needed. Do not copy `PackageSources` or merge old/new files. The installed folder must directly contain `manifest.json`, `layout.json`, `InGamePanels` and `html_ui`. Use Developer Mode → Tools → Virtual File System → Packages Folders → Open Community Folder to locate it if needed. Do not install another copy in Community2024; its compatibility remains unverified.
2. Restart MSFS and load a desktop flight. The user already confirmed built-in panels move/resize normally; briefly repeat that comparison in the new session to retain a controlled baseline.
3. Open AeroScience. The full panel has Mission/Payload only, with no Compact VR control. Test the outer title bar, each supported edge/corner and scrolling without the manual initializer. If testing the probe instead, it must say **NATIVE WINDOW PROBE — NO MISSION CONTROLS**; its engine is unnecessary and its bordered scroll box should reach END. Record failures before any diagnostic style/initialization change.
4. If a full/probe comparison is needed, close MSFS, replace the whole package with the other output, and restart/load the same flight. If old assets appear, verify the copied output and full simulator restart before testing.
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
5. In Elements, inspect the **actual generated** `ingame-ui-header`, frame wrappers and resize-looking nodes recorded in the JSON. Check Event Listeners for the title bar and left edge if that debugger version provides it. In the debugger's **Debugger** tab (or its source-file browser), search the installed `common.js`/`ingameUi` scripts for `DragDropHandler`, `ResizeHandler`, `startDrag` and `startResize`. Break at the relevant method or mouse-down event breakpoint if available, reproduce, and record the native file/line, call stack, target/currentTarget, bound drag element/current handle and game-handling flag. Names may differ by installed build: inspect rather than guess. Resume promptly; stopping JavaScript delays panel polling. Do not redistribute the proprietary script implementation.
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
