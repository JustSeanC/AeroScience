# SDK toolbar implementation and verification boundary

The official MSFS 2024 SDK builds the Community package; source XML is not a compiled
add-on. Follow [Windows build/installed acceptance](public-release-windows.md). The
existing Mission/Payload/Map interface uses native SDK window controls and request/reply
CommBus, with no simulator write-control or localhost browser embedding.

Historical diagnosis established that `/JS/dataStorage.js` must load before the native
templates initialize drag/resize handlers. Preserve that bootstrap and the
[debugging record](toolbar-interactions.md). Earlier user-reported POC panel loading,
replies and dragging are historical evidence, not new MSFS/Quest acceptance.

## Research and implementation choice

The official entry point supplied by the user is [MSFS 2024 SDK Introduction](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/Introduction.htm). Consult its SDK navigation for **InGamePanels**, **Package Tool / SPB asset groups**, **JavaScript → Communication API**, and [SimConnect Communication API](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Communication/Communication_API.htm). The initial cloud review encountered HTTP 403 for that host; official JavaScript/debugger pages were accessible during the current interaction investigation. Public documentation does not expose the installed native window implementation.

Public evidence consulted (reference only; not a substitute for an official SDK/simulator build):

- [Microsoft's MSFS 2024 avionics mirror](https://github.com/microsoft/msfs-avionics-mirror), including toolbar listener types, at commit `366be5056166c639a2189e09e5af7143174fd910`.
- [Dutch FlightSim Developer: CommBus from Toolbar](https://github.com/Dutch-FlightSim-Developer/developing-with-simconnect/tree/main/part-13/13-4%20-%20CommBus%20from%20Toolbar), a reported simulator-tested SPB/Copy toolbar example, plus its companion C++ example. It documents `RegisterCommBusListener`, `callSimConnect` and simulator-owned UI templates.
- [VR Checklist SDK research](https://github.com/PatUlm/msfs2024-vr-checklist/blob/main/docs/msfs-sdk-reference.md), including native .NET bindings, SDK-version notes, JavaScript load lifecycle and runtime observations.
- [node-simconnect Communication API issue](https://github.com/EvenAR/node-simconnect/issues/120) and [native CommBus API reference implementation](https://github.com/mrlm-net/simconnect/blob/main/internal/simconnect/commbus.go).

These establish a concrete SDK-format implementation path, not proof that AeroScience has been loaded in MSFS. No unsupported localhost access or browser embedding is assumed.

## Transport

`Include.addScript('/JS/Services/CommBus.js', callback)` loads the simulator's helper, then `RegisterCommBusListener()` subscribes to `AeroScience.Reply.v1`. The toolbar calls `callSimConnect('AeroScience.Command.v1', json)`. The native bridge subscribes through `SimConnect_SubscribeToCommBusEvent` and replies through `SimConnect_CallCommBusEvent` with the JS broadcast flag.

Each request has `protocol:1`, a GUID `requestId`, and `op:'state'|'start'|'end'`. Start includes `name` and `mode:'universal'|'realistic'`; end includes the active `missionId`. Replies correlate by GUID and include the same engine snapshot consumed by the web application. Requests are bounded, mutating retries are deduplicated, and a wrong mission ID cannot end another mission. The full protocol logic is exercised by C# tests; browser tests mock the SDK to test the UI contract. Neither test is a simulator acceptance test.

The panel asks for current state once per second only while visible. The engine sends **only responses**, avoiding an unsolicited event backlog while simulator JavaScript is suspended/debugged. Callbacks copy bounded packet data; mission/database processing happens after dispatch. Commands must fit one CommBus message (maximum 4 KB wire input, 1,024 JSON characters); fragmented commands are rejected. Replies are bounded at 8 KB. There are no sockets, HTTP requests, extra WASM module or per-aircraft scripts in the toolbar. Simulator-local add-ons are trusted operators; the LAN pairing boundary is separate.

## Maintainer SDK builds

1. On Windows, install the current official MSFS 2024 SDK and use an x64 Native Tools shell with PowerShell 7, Python 3 and the pinned .NET SDK.
2. Build the v0.1.1 Windows app with `Build-Companion.ps1`, then launch `AeroScience.exe` while the simulator is running. Import the official native runtime through Settings for personal testing if necessary. Production simulation and CMD launchers have been removed; an offline dashboard/archive does not require MSFS.
3. Run `pwsh scripts/build/Build-Toolbar.ps1 -SdkRoot 'YOUR SDK ROOT' -ProofOfConcept`. It stages a copy, compiles the native ABI contract against the official headers, invokes `fspackagetool.exe`, validates generated files and writes an external SHA-256 build receipt.
4. If tool paths differ in the installed SDK, use its Project Editor to open the staged `AeroScience.xml`, build the package and inspect its report; update the script from the SDK's documented tool paths/arguments. Windows script execution is pending here.
5. Install only the generated package from the build output. Restart MSFS, load any aircraft, and verify the AeroScience icon, floating/resizable panel, and a changing JSON reply. Use SDK Coherent/dispatcher diagnostics to distinguish registration, script-loading, native connection and protocol problems.
6. Validate the proposed Community2024 location. The legacy InGamePanels project must not be declared compatible with Community2024 without that check. If the SDK supports it only in Community compatibility mode, record that evidence and ship there instead. Test Store and Steam package discovery; do not hardcode a single path.
7. Build without `-ProofOfConcept`, then check telemetry, both mode selections, browser/toolbar start/end synchronization, close/reopen retention and disconnect/reconnect behavior.

The SDK produces `manifest.json`, `layout.json`, `InGamePanels/aeroscience_panel.spb`, UI files and icon. The release validator checks versions, layout coverage/sizes/FILETIME, internal expected assets and binary registration presence. The SDK build receipt hashes every file; neither a POC nor window-probe receipt can pass the full-release gate. A structural check alone cannot prove simulator loading.

The script retains `sdk-contract.log` and `build.log`, uses its staged project as the working directory, supports explicit tool/header paths, and normalizes receipt file paths with the platform separator. The Windows receipt separator bug from the initial foundation has been corrected. Portable tests exercise compiler failure/log retention. The gaming-PC POC and previous full panel loaded with recurring replies/synthetic data; the v0.1.1 revision's SDK build, native input retest, mission-command and long-duration/lifecycle checks remain pending. See the [Windows workbook](https://github.com/JustSeanC/AeroScience/blob/8f510f02f4d9b41fba4e55fefa950b82e48c132d/docs/windows-testing.md) and the [interaction retest](toolbar-interactions.md).

## VR / input

Mach is shown alongside indicated airspeed when available, using the shared nullable field. Its actual simulator reception and layout need Windows acceptance after the extended ABI contract passes.

The panel uses the simulator's `ingame-ui`, header and native push-button templates for its floating, resize and input behavior. AeroScience uses one responsive interface with high contrast text and controls at least 48 px high. Compact VR has been removed. Desktop simulator testing verifies native movement, resizing, wheel input and mission controls. VR-specific acceptance measures frame rate/frame times, stutters and companion/toolbar resource impact only; there is no separate headset interaction matrix. A Chromium screenshot cannot establish native input or headset performance. The disconnected lifecycle releases listeners/timers; closing the panel sends no mission-end command.
