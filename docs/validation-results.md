# Validation results — 2026-10-16

Initial cloud validation was performed on Linux. Separately identified gaming-PC results below come from the user's supplied Windows logs. Build results do not establish simulator loading, companion execution, physical-device or published-environment acceptance.

| Check | Outcome | Scope |
| --- | --- | --- |
| `bash scripts/setup-cloud.sh` | Passed | Pinned SDK activation, frozen NuGet/npm restore, Release build and static JS check; no source/lockfile edits |
| `dotnet test AeroScience.sln -c Release --no-restore` | **23 passed, 0 failed, 0 skipped** | Missions, telemetry, SQLite, mode selection/defaults/migration/restart, API/SSE, security, shared toolbar protocol and packet handling |
| `npm run test:web` with existing Chromium | **5 passed, 0 failed, 0 skipped** | Live web workflow, multiple displays, phone/tablet layouts, display-independent recording; toolbar test mocks SDK globals |
| Python packaging unit tests | **4 passed, 0 failed** | Invalid source packages/layouts/provenance/acceptance and ZIP overwrite protection |
| `scripts/build/smoke_recovery.py` | Passed | Real process killed/restarted; at least 2 samples and Realistic mode retained; Universal default/completion retained; competing owner refused |
| Self-contained `win-x64` publish | Passed | Native x64 PE apphost, engine/runtime/database libraries and hosted web assets produced |
| Companion preview ZIP | Passed | ZIP CRC/one-root inspection, x64 publish validation; no database/log files, Community package or proprietary SimConnect DLL |
| NuGet transitive vulnerability report | No known vulnerabilities reported | Current configured official NuGet sources at validation time |
| npm production audit | No known vulnerabilities reported | Application has no production npm dependencies |
| Toolbar XML / JS | Syntax checks passed only | Not official SDK compilation, package validation or Coherent rendering |

The .NET SDK bootstrap archive was verified against Microsoft's published SHA-512. The initial vulnerable native SQLite dependency was upgraded; auditing remains enabled.

Generated evidence: `tests/AeroScience.Tests/TestResults/foundation.trx`, `artifacts/web-results.json`, `artifacts/dashboard-desktop.png`, `artifacts/dashboard-tablet.png`, the preview ZIP and SHA-256 sidecar. These are ignored development artifacts, not source commits. The ZIP contains only published companion files and user documentation.

**At the initial Linux review, unrun/pending:** Windows launch/clean-machine installation; official SDK ABI compilation and toolbar package build; actual Community/Community2024 installation on Store/Steam; real SimConnect flights/lifecycle/units; real CommBus/Coherent panel behavior; Quest 3 PCVR; physical iPad Safari/LAN; full release acceptance. PowerShell/SDK scripts and CI configuration were authored but not executed on their target Windows/GitHub runners here. See the later gaming-PC reports below and `verification.md` for current status.

The official SDK documentation URL supplied by the user returned a proxy access denial. A custom domain addition plus tested install/start instructions were saved to the cloud environment draft. Saving did not apply that network change, publish a snapshot or validate restoration in a new task.

## Windows installation-readiness review

- Re-ran the .NET suite after loader-diagnostic changes: **23 passed, 0 failed, 0 skipped**, recorded in `windows-readiness.trx`.
- Python discovery with PowerShell 7 available: **10 passed** (5 packaging tests plus 5 helper checks). Checks cover parsed PowerShell scripts, selected feedback/log collection with database/SDK exclusion, PE rejection/metadata-only boundaries, retained compiler-failure logs without a receipt, and runtime-free companion preview inputs.
- PowerShell 7.6.0 was installed on Linux from Microsoft's release archive and verified against its SHA-256 manifest. This enables syntax and portable/helper-mock checks; it does **not** establish Windows PowerShell 5.1 execution, real MSVC compilation or SDK behavior.
- The Windows receipt separator error was fixed; SDK header paths/tool overrides, compiler/package logs, runtime-check reports, clean simulated launcher defaults and feedback helpers were added. No SDK DLL was downloaded or redistributed.
- Windows launcher execution, real `cl.exe`/installed SDK contract, actual native `SimConnect.dll` loading/exports and all simulator/VR/iPad checks remain **pending on the gaming PC**. See `windows-testing.md`.

## First reported Windows SDK build

- The user's Windows compiler reached `sdk-contract.cpp` and reported `C2607` at the assertion `sizeof(SIMCONNECT_RECV_COMM_BUS) == 32`. This is a failed build, not SDK/package acceptance.
- Official Microsoft documentation was accessible during troubleshooting: [SIMCONNECT_RECV_COMM_BUS](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Structures_And_Enumerations/SIMCONNECT_RECV_COMM_BUS.htm) inherits the [list header](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Structures_And_Enumerations/SIMCONNECT_RECV_LIST_TEMPLATE.htm), adds `uEventID`, then a variable-length `rgData` string. The original check confused the fixed header/payload offset with the complete declared struct size.
- Replaced that size assumption with checks for the actual fragment, subscription and payload offsets used by the decoder; also asserted the telemetry payload offset. No runtime decoder or companion binary changed. Recompilation against the user's installed SDK and simulator testing remain pending.
- Re-ran the five portable PowerShell helper tests: **5 passed**, including compiler-failure log retention and refusal to issue a successful build receipt on failure. These do not compile against the Windows SDK.

## First reported SDK package output

- After the contract fix, the user's screenshot shows MSVC compilation continuing into the official package builder, the panel SPB compiling, `Validation SUCCESS`, and zero failed SDK build tasks. The log identifies game version `1.9.18.0` and package validator `1.0.7.0`; these are not an identified SDK installer version.
- The user's SDK-generated manifest reports `builder: "Microsoft Flight Simulator 2024"`, package version `0.1.0`, no dependencies, and `minimum_game_version: "1.9.18"`. Our post-build checker incorrectly demanded four components and prevented the build receipt from being written.
- Corrected that checker to accept three or four numeric components while retaining missing/malformed-version rejection, layout/asset checks, and the separate release acceptance requirements. The SDK-generated manifest is not modified. Added regression coverage for both accepted forms and invalid/missing values.
- Packaging regression suite after the correction: **7 passed**, including the reproduced three-component rejection fixed by this change. Test assets are structural fixtures, not official SDK output or simulator evidence.
- This evidence establishes user-reported SDK contract compilation and Microsoft package validation. Our complete script/receipt, installation, panel loading/transport, live telemetry, VR and iPad acceptance still need their respective checks.

## Reported SDK layout casing

- The next user-supplied build log again reports Microsoft `Validation SUCCESS`, followed by our checker rejecting lowercase `html_ui/icons/toolbar/icon_aeroscience.svg`.
- The supplied layout and file listing confirm seven lowercase layout paths with corresponding mixed-case physical names, including `ICON_AEROSCIENCE.svg`, `AeroScience.html`, and `InGamePanels/aeroscience_panel.spb`. The package contains nine files in `artifacts/toolbar-poc/output/Packages/aeroscience-toolbar`.
- Corrected only package path matching to be case-insensitive. Original names and bytes remain intact for SDK build receipt hashes. Missing files, incorrect sizes, layout self-reference, case-only duplicate entries and case-colliding physical files remain rejected. Applied the same matching to required assets and their reads to avoid a subsequent false rejection after layout validation.
- Python regression discovery with PowerShell available: **18 passed** (13 packaging checks and 5 portable helper checks). Tests reproduce lowercase layout entries against mixed-case files and lowercase output assets, and verify rejection of missing/incorrect/duplicate paths. The SDK-provided HTML/CSS/JS/icon sizes in the user's layout also match the checked-in sources.
- Full script/receipt completion, package installation and simulator/device behavior still require the gaming-PC checks; this correction does not establish them.

## Successful gaming-PC proof-of-concept build

- The user's subsequent complete PowerShell output confirms `sdk-contract.cpp` compilation, official package-tool `Validation SUCCESS` with zero failed tasks, `Community structural validation passed: 9 files`, and the final `SDK-built output` message. The proof-of-concept build script completed its receipt-writing stage without a reported error.
- Output: `C:\Dev\AeroScience\artifacts\toolbar-poc\output\Packages\aeroscience-toolbar`. SDK root shown in the log: `C:\MSFS 2024 SDK`; game `1.9.18.0`; package validator `1.0.7.0`. The exact SDK installer version remains unreported.
- Preserve `artifacts/toolbar-poc/sdk-contract.log`, `build.log` and `sdk-build.json` on that PC with the generated package. The cloud has received console text and metadata, not the actual SPB or receipt contents.
- **Passed, user-reported:** installed-header contract compilation and the complete POC package-build/structural-validation script. **Still pending:** Community folder choice/installation, panel registration/rendering, CommBus replies, full toolbar build, Windows companion/live telemetry, Quest 3 and physical iPad checks. No full release has been published.

## Proof-of-concept panel opened in MSFS

- The next user screenshot shows the native `AEROSCIENCE` panel over an actual MSFS flight, displaying `AeroScience registration & CommBus probe`, the read-only explanation and `Waiting for SDK communication service…`.
- **Confirmed by screenshot:** simulator registration and visible desktop rendering of the diagnostic panel. The installed folder path, simulator distribution, aircraft identity, icon appearance, resizing/dragging and VR interaction are not established by this image.
- The waiting label is the probe's initial text. It does not distinguish an uninitialized JavaScript service from an absent companion/reply. CommBus request/reply, runtime DLL loading, Windows companion execution and live aircraft telemetry remain pending.
- Next: locate/check the SDK's native x64 `SimConnect.dll`, copy it beside the chosen companion executable for personal testing, then run simulated telemetry with the toolbar bridge explicitly enabled. Verify changing JSON replies before advancing to the full panel or live telemetry.
- The user's SDK file listing identifies `C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll` (84,480 bytes). This establishes its presence only; architecture, actual Windows loading, exports and simulator connectivity still need checking.

## First visible CommBus reply

- Following the native-DLL/bridge setup instructions, the next MSFS screenshot replaces the initial waiting label with JSON beginning `{"protocol":1,"requestId":"…","ok":…}` in the diagnostic panel. The remaining reply is clipped horizontally in the raw diagnostic view.
- **Confirmed by screenshot:** a companion protocol reply reached the in-simulator panel, establishing a POC CommBus request/reply round trip in this session. This also demonstrates execution of the companion's reply path and its native CommBus transport; it does not establish the standalone runtime checker's complete export report, clean-machine installation or live aircraft telemetry.
- The user subsequently confirmed that the JSON request ID keeps changing about once per second while the panel stays open. Recurring POC polling/replies are therefore user-reported passed; the length of this observation was not measured.
- Full reply contents, long-duration stability, simulator disconnect/reconnect behavior, mission mutations and cross-display synchronization still require their own observations. Real aircraft telemetry, full panel, VR and iPad checks remain pending.
- Next: build/install the full panel and test Universal/Realistic missions with simulated telemetry and browser synchronization before selecting live SimConnect telemetry. See the [v0.1.0 testing handoff](v0.1.0-testing-handoff.md).

## Full toolbar baseline and interaction revision

- The user's next screenshot and uploaded requirements confirm the previous full panel was built/installed/opened, with simulated altitude/airspeed/heading/vertical speed and an active Universal mission display (12 seconds, 9 samples). This supersedes the earlier diagnostic-only milestone; it does not prove all start/end operations, browser synchronization or persistence on that PC.
- The user reported inability to drag, resize or scroll. Those baseline desktop interactions are **Failed**. Quest 3 results remain **Not yet tested**.
- Implemented registration attribute corrections, SDK-owned frame bounds with responsive content/scrolling, and a header Compact VR checkbox. The exact native dragging failure remains uncertain without a simulator retest; no custom drag or wheel interception was introduced. See [investigation and acceptance records](toolbar-interactions.md).
- .NET regression: **23 passed**, `toolbar-interactions.trx`. Complete browser run: **9 passed** (4 dashboard plus 5 mocked-toolbar checks); the 5 toolbar checks also passed after the final narrow-header adjustment. Python packaging/helper discovery: **18 passed**. JavaScript syntax and XML parsing passed.
- Browser tests exercise supplied bounds at 320×360, 640×720 and 900×900, wheel scrolling to mission controls, layout/page/state retention, recurring mocked data and hidden/reconnected panel behavior. Fixture screenshots are explicitly labeled as browser-only. These tests do not verify native title-bar movement, resize handles, Coherent wheel routing or VR.
- The cloud has no official Windows SDK package tool/simulator. This revision's SDK package build is **Blocked here** and its desktop/Quest 3 acceptance is **Not yet tested**. No updated SPB/manifest/layout/receipt or Community archive is claimed. The Windows instructions use the existing validated SDK build script to generate them on the PC.
- Engine location supplied by the user is now `C:\Windows-Companion-unverified-in-MSFS`. No companion engine code or distribution was changed for this interaction revision.


## Native interaction retest after `9a1946d`

- The updated full-panel screenshot shows the temporary header layout control and synthetic metrics. The user reports normal dragging/resizing still fail. A resize cursor appears near the left edge, but dragging there moves the entire window instead of changing its dimensions. This is confirmed reported behavior, not a diagnosed native handler/CSS cause.
- **Passed, user-reported comparison:** built-in panels drag and resize normally in the same MSFS session. This narrows the failure toward the add-on integration/template path; it does not identify the exact target, binding or layout defect.
- Scrolling was not successfully verified in this latest retest; clipped content alone does not establish whether wheel events arrive. Earlier scrolling failure remains recorded above.
- Full mission start/end, live-aircraft telemetry and persistence on the gaming PC remain separately unverified. No native interaction pass is inferred from successful package compilation, rendering or browser tests.

## Current native-window investigation and feature removal

- Removed Compact VR and its obsolete assertions/styles/logic. Desktop and VR share one responsive interface. Current VR-specific acceptance is performance only; historical headset-interaction notes above are not current requirements.
- Preserved production registration/native host geometry and all engine/mission/CommBus code pending actual evidence. Added a separate minimal-window SDK build and a passive debugger capture. Neither is claimed simulator-tested; no root cause or native correction is yet established.
- .NET: **23 passed**, zero failed/skipped, `tests/AeroScience.Tests/TestResults/native-window-investigation.trx`.
- Complete browser suite: **11 passed**, zero failed/skipped, `artifacts/web-results.json`. This includes four real-engine dashboard workflows, the retained mode/start/end protocol test, four single-interface layout/lifecycle checks and two passive-capture utility checks. The initial run found one obsolete Compact VR assertion in the protocol test; it was removed while preserving the mode/start/end assertions, then the complete suite passed.
- Python discovery with PowerShell available: **21 passed**, zero failed/skipped. Coverage includes the existing layout/version/path/receipt gates and helper checks, the window-probe release refusal, performance-evidence requirement and rejection of conflicting diagnostic flags before touching existing generated output.
- All toolbar JavaScript/capture syntax checks, three HTML pages' local references/unique IDs, matching full/probe native host attributes and three project/registration XML parses passed. These checks do not compile the SDK package or prove native interaction.
- Actual-process recovery smoke passed: retained Realistic mode and two committed samples after a process interruption; rejected a duplicate engine; preserved Universal defaults and completed history. Dashboard JavaScript syntax, documentation links/code fences and `git diff --check` passed. Companion source, the POC HTML/JS and production panel registration were unchanged.
- Windows SDK/MSFS are unavailable in this Linux environment. New full/probe package compilation, generated SPB/manifest/layout/receipt and actual native call stacks/input remain **Blocked here / Not yet tested on the PC**.
- Follow [the current comparison, debugger and desktop checklist](toolbar-interactions.md) before another proposed native fix. Reuse `C:\Windows-Companion-unverified-in-MSFS`; no engine rebuild is needed.

## Windows native-window probe build and first simulator test

- Source: `b7b42d807880b9095899b9c4ff753aa92857b3a0`, variant `toolbar-window-probe`. The user's Windows output reports zero failed SDK tasks, eleven-file structural validation and script completion through `SDK-built output`. This establishes a user-reported successful probe build; its generated files remain on that PC.
- Output: `C:\Dev\AeroScience-WindowProbe-20261016-182132\AeroScience-b7b42d807880b9095899b9c4ff753aa92857b3a0\artifacts\toolbar-window-probe\output\Packages\aeroscience-toolbar`.
- The user reports that the installed probe's text scrolls and that top/left dragging moves the window. The supplied 11.3-second video shows the probe rendered at changing positions, missing close/pop-out controls, and a gray band below its content. Scrolling is user-reported evidence for the probe's fixed scroll box, not the full mission UI.
- Correct native edge resizing remains **unverified**. Movement from a resize-looking region must not be classified as a successful resize without before/after dimensions and actual native event targets. Complete normal title-bar/control behavior is also not established.
- Source comparison found that the probe omits `/JS/buttons.js` and `/templates/NewPushButton/NewPushButton.html`, which the full toolbar includes. Thus the earlier description of identical native imports was too broad: frame/header imports and host attributes match, but button dependencies differ. This confounds header-control comparison; it is not yet a demonstrated cause of the missing controls or resize failure. Capture initialization errors and custom-element readiness before another correction.
- Next evidence: the installed Coherent view's console errors, generated frame/header/handle geometry and mouse event paths using the existing opt-in capture. Built-in panels were already reported to move/resize normally. No further production CSS or native handler changes are made from the video alone.

## Coherent probe inspection and button dependency correction

- The user's debugger screenshot confirms page `coui://html_ui/InGamePanels/AeroScience/WindowProbe.html`, `buttonTemplateLoaded: false` for `new-push-button`, and `headerTemplateLoaded: true` for `ingame-ui-header`. This establishes missing button registration in the installed probe. Its causal relationship to missing native controls still requires a controlled retest; it does not explain the full toolbar's resize defect, since the full toolbar already includes the button dependencies.
- The eight displayed 404 errors reference `.map` source-map files. They do not by themselves establish failure to load the corresponding runtime JavaScript/CSS. The live `ingame-ui` element is initialized and shows native-applied minimum attributes `22`/`30`; their relationship to actual geometry requires capture, not an assumed size fix.
- Corrected the probe's two missing imports, `/JS/buttons.js` and `/templates/NewPushButton/NewPushButton.html`, matching the full toolbar's native dependency order. No production toolbar, engine, window geometry, handler or protocol change was made.
- Python discovery with PowerShell: **22 passed**, zero failed/skipped. The new source-bootstrap parity test also rejects a temporary reconstruction of the original incomplete probe, and passes with the corrected imports. The final parser readability adjustment passed the targeted test again. This checks the controlled comparison's declared bootstrap, not actual SDK imports or native control behavior. Earlier .NET/browser evidence applies to unchanged production code; those suites were not repeated for this probe-only import correction.
- Corrected probe SDK compilation, button readiness/header controls and native movement/resizing remain **Not yet tested on Windows**. Preserve the earlier probe's logs/capture, rebuild using the official script, fully replace/restart the package and repeat readiness and native input checks. The existing companion is reused.

## Probe retest after `abb8982`

- The user confirms the buttons are now visible. The supplied console result identifies `WindowProbe.html` with both `buttonTemplateLoaded: true` and `headerTemplateLoaded: true`. This verifies restored button registration/visibility for the corrected probe, not activation of every header control. No new complete SDK build log was supplied with this report.
- The user still cannot drag from the header and reports mixed resizing/movement at the top and left edges. Header dragging remains user-reported failed. Edge behavior is not yet classified from dimensions: left/top resizing normally changes the left/top position while changing width/height, keeping the opposite edge fixed. Translation alone or an unexpected opposite-edge shift is a separate result.
- Collect one left-edge before/after capture in the currently installed probe, including actual hit targets and frame/wrapper bounds. No further rebuild or speculative production layout change is needed to obtain this evidence. Native handler call stacks remain the next diagnostic if DOM geometry cannot explain game-owned input/position routing.

## First exported native geometry/input capture

- The user supplied the complete version-1 JSON capture for `WindowProbe.html`: five labeled snapshots, seven input entries and seven delayed observations (19 entries, within the 120-entry limit). The earlier console-history attachment did not include expanded geometry; this exported JSON does.
- All three captured header presses target `ui-button.wrap.not-highlightable` under `ingame-ui-header`, with normal pointer events. The header's local rectangle is `(4,4,1162,56)` before resizing. Thus the captured presses reach the native header wrapper; this does not prove its movement callback ran or record the game window's screen position. Header movement remains user-reported failed.

| Quantity | Before header drag | After header drag / before left edge | After left edge |
| --- | --- | --- | --- |
| Document box width | 2787 | 2787 | 3096 |
| Document box height | 432 | 432 | 432 |
| Frame width | 1170 | 1170 | 1300 |
| Frame height | 360 | 360 | 360 |
| Frame local x/y | 0/0 | 0/0 | 0/0 |

- Confirmed sizing mismatch: frame widths agree with the source's `42vw` rule to pixel rounding (`2787 × .42 ≈ 1170`, `3096 × .42 ≈ 1300`). The `55vh` height is clamped by the source's 360 px minimum inside the 432 px document. The simulator changes the view/document width while our frame follows at 42 percent. Both the probe and production frame declare these dimensions; native inline styling in this capture contains minimum-size variables, not a width/height override. The earlier assumption that native inline geometry would supersede the fallback is unsupported in this installed template.
- The left-edge attempt has a frame-targeted `mouseup`, followed by the measured width change; no left-edge `mousedown` was captured. Do not infer an incorrect DOM drag binding from that absence. Native screen coordinates/anchors and callback routing remain unmeasured; frame-local x/y cannot establish which screen edge stayed fixed.
- Next controlled test: temporarily set only frame width/height to 100 percent in the live debugger, keep existing minimums and native handlers, and compare bounds and behavior while enlarging above the current minimum. A browser-only check reproduces the measured 42-percent scaling and verifies the temporary sizing/restore snippet; it does not verify MSFS input. No package/source behavior change or new build is made pending the live result.

## Working Weather and VSR window comparisons

- Weather's selected page is `coui://html_UI/Global/Panel.html`, document title `Weather`, with a 951×1254 viewport. Its root `UI-PANEL` fills that viewport at `(0,0,951,1254)` beneath `ENTRY-POINT`. The report contains no iframes and no shadow roots among the inspected nodes; the loaded script is `Global/panel.bundle.js`. This is a different component structure from AeroScience's legacy `ingame-ui`; a missing legacy element in that page is not itself a defect.
- The user supplied a closer third-party reference: `coui://html_UI/ingamePanels/vsr/vsr.html`, using `INGAME-UI` and `INGAME-UI-HEADER`, and explicitly confirms title-bar movement and edge resizing work normally in the same MSFS session.
- VSR's viewport and frame are both 1192×750 with frame local x/y 0/0. It has `content-fit="true"`, classes `ingameUiFrame fittedContent collapsible draggable`, and inline `left:0px; top:0px; width:100vw; height:100vh`. Its frame display is flex. Its native header is 56 px high, with a `UI-BUTTON.wrap` and the usual header methods. The current AeroScience probe has `content-fit="false"`, lacks `fittedContent`/`draggable`, and scales only part of its own viewport. Both source variants declare the false content-fit setting.
- These working comparisons support full-view frame sizing and identify a native configuration difference relevant to movement. They do not yet prove which SDK condition creates the draggable state, nor establish a corrected AeroScience input pass. The next read-only check is the installed SDK frame's initialization, content-fit accessor and attribute-update methods, rather than manually forcing native classes or adding custom movement code. No further Weather inspection or build is required for that check.
