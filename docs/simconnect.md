# Windows SimConnect integration

Current v0.2.0: see [SDK research](sdk-research-v0.2.0.md), [new Windows acceptance](v0.2.0-windows-testing.md) and [completion status](v0.2.0-completion.md). Earlier results below are historical and do not verify the changed release.


The provider uses the **native** `SimConnect.dll` API from the official MSFS 2024 SDK. It avoids the older mixed-mode managed .NET Framework wrapper. The user's gaming-PC screenshot demonstrates a companion CommBus reply reaching the in-simulator POC panel, and the user confirms recurring request-ID updates about once per second. Initial real-aircraft reception/display and an active recording were subsequently observed with T-38N WSSP Late. The new Mach field, completed live-history/export workflow, standalone runtime-checker report and clean-machine distribution remain unverified; see the [evidence record](validation-results.md#first-live-aircraft-telemetry-and-active-recording-evidence).

Official starting references:

- [MSFS 2024 SDK introduction](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/Introduction.htm)
- [SimConnect SDK](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/SimConnect_SDK.htm)
- [Communication API](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Communication/Communication_API.htm)

The official documentation host was blocked by this cloud environment's egress proxy during initial development; it became accessible during gaming-PC build troubleshooting. API constants/signatures were initially cross-checked against the public reference implementations listed in `toolbar.md`; the **installed official SDK remains the authority**. `scripts/build/sdk-contract.cpp` checks native member/payload offsets, struct sizes, receive IDs, datatypes, period, broadcast flag and available function declarations against that SDK on Windows. The user's complete POC build log now confirms that installed-header compilation passed. The extended v0.1.1 contract requires a fresh installed-SDK compilation; runtime export inspection and complete lifecycle acceptance remain separate checks; see the [evidence record](validation-results.md).

## Telemetry definition

One fixed data definition uses `TITLE` (`STRING256`), followed by 13 `FLOAT64` values. Mach is appended after the original twelve, preserving their offsets:

| SimVar | Units / interpretation |
| --- | --- |
| `PLANE LATITUDE`, `PLANE LONGITUDE` | degrees |
| `PLANE ALTITUDE` | feet MSL |
| `PLANE ALT ABOVE GROUND` | feet AGL; nonfinite values become null |
| `AIRSPEED INDICATED`, `AIRSPEED TRUE`, `GROUND VELOCITY` | knots |
| `PLANE HEADING DEGREES TRUE`, `GPS GROUND TRUE TRACK` | degrees true, normalized 0–360 |
| `PLANE PITCH DEGREES`, `PLANE BANK DEGREES` | degrees; SDK sign converted to nose-up/right-bank positive display convention |
| `VERTICAL SPEED` | feet/minute |
| `AIRSPEED MACH` | `mach`, actual simulator-provided Mach number |

The packed client payload is 360 bytes: title at offset 0, the original twelve doubles at offsets 256–344, and Mach at offset 352. The receive envelope adds 40 bytes before the payload. Decoder tests cover each field, bounds and unavailable Mach. Negative/nonfinite Mach becomes null without discarding the other valid fields; it is never calculated from IAS. Historical samples lacking this JSON field remain readable. The [official aircraft variables reference](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimVars/Aircraft_SimVars/Aircraft_Misc_Variables.htm#AIRSPEED%20MACH) documents this SimVar. The installed-header contract must pass again on Windows after this definition change.

`TITLE` is the loaded aircraft title; it is not assumed to be an ICAO model code. No reliable separate model identifier is currently exposed. Missing titles use an explicit fallback. No aircraft whitelist, aircraft-specific SDK definitions, fixed-wing speed minimum, or aircraft-file modification is involved. Invalid position/required numeric frames are dropped instead of recorded as plausible-looking data. Compatibility and sign/unit conventions must still be verified in actual fixed/rotary-wing flights.

The provider opens a local connection, subscribes to `SimStart`, `SimStop`, `Pause`, `AircraftLoaded`, `FlightLoaded`, requests the `Sim` system state and requests the user's aircraft at `SIMCONNECT_PERIOD_SECOND`. It waits for running state and a valid frame before publishing connected telemetry. Aircraft/flight transitions invalidate readiness. An eight-second stale-data timeout changes status to waiting, clearing live data; paused/loading periods are not filled with synthetic samples. Native calls and dispatch are serialized with a 50 ms asynchronous wait. Actual disconnections/quit/runtime errors are reported and retried after five seconds. Exceptions never cross the native callback boundary.

## Runtime and distribution

The public Windows installer bundles the authorized native x64 runtime beside AeroScience.exe and installs the supported Microsoft Visual C++ prerequisite when needed. The maintainer build validates the official SDK DLL's hash, architecture and required exports. Testers do not import SDK DLLs or copy runtime files manually; run Setup again to repair missing application files. The published app has no synthetic provider or fallback. Without the runtime or simulator, the dashboard and local archive remain available in a clear disconnected state.

This project does not include Microsoft's proprietary DLL, assume redistribution permission, or require end users to install a development SDK. Before a complete public release, the maintainer must review the installed SDK's redistribution/license terms and establish a permitted runtime-delivery method. The full-release packaging validator requires the runtime and explicit redistribution confirmation; the companion-only preview deliberately excludes it. If redistribution is not permitted, that is a full-release blocker that needs a supported Microsoft runtime installation route.

For the current review, `Test-SimConnectRuntime.ps1` checks a supplied SDK DLL's PE architecture/managed header and (on Windows x64) actual loading and all used native exports. Record its report along with `sdk-contract.log`; the DLL check is still pending on the gaming PC. The source ABI review uses 32-bit HRESULT/DWORD/flags/byte counts, x64 handles/pointers and serialized dispatch, and the C++ contract checks installed declarations/layouts before package building. Successful header compilation does not establish DLL loading or a working simulator connection.

If a DLL exists but its dependent Microsoft Visual C++ runtime is missing, loader diagnostics now distinguish that case from an absent `SimConnect.dll`. The Windows workbook provides the official Microsoft runtime guidance and exact local testing procedure.

`EnableToolbarBridge=false` disables the CommBus worker during focused telemetry diagnosis. Its default is true on Windows; failures are logged without stopping recording. The bridge requires the MSFS 2024 SU5+ CommBus API (public reference evidence identifies SDK 1.6.4+, to be confirmed against the installed SDK/runtime). An older runtime missing those exports is diagnosed separately from the basic telemetry connection.

## Recording cadence

The provider requests actual observations at `SIMCONNECT_PERIOD_SECOND`. The coordinator schedules recording slots from the first sample and tolerates timestamp jitter by 10% of the configured interval, capped at 100 ms. It accepts at most one real observation per slot, preserves its original timestamp, rejects duplicates/backwards timestamps and paused frames, and records missing intervals when observations resume. It never generates replacement observations. Diagnostics separate configured interval, observed sample cadence, mission-wide average and gaps. The reproduced old threshold behavior and automated regression are described in [alpha testing](alpha-testing.md); sustained live cadence remains unverified.
