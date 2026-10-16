# Windows SimConnect integration

The provider uses the **native** `SimConnect.dll` API from the official MSFS 2024 SDK. It avoids the older mixed-mode managed .NET Framework wrapper. This is a compiled implementation; real MSFS operation and runtime loading have **not** been tested here.

Official starting references:

- [MSFS 2024 SDK introduction](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/Introduction.htm)
- [SimConnect SDK](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/SimConnect_SDK.htm)
- [Communication API](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Communication/Communication_API.htm)

The official documentation host was blocked by this cloud environment's egress proxy during initial development; it became accessible during gaming-PC build troubleshooting. API constants/signatures were initially cross-checked against the public reference implementations listed in `toolbar.md`; the **installed official SDK remains the authority**. `scripts/build/sdk-contract.cpp` checks native member/payload offsets, struct sizes, receive IDs, datatypes, period, broadcast flag and available function declarations against that SDK on Windows. The user's complete POC build log now confirms that installed-header compilation passed. Native DLL loading/exports and actual simulator transport/telemetry remain separate pending checks; see the [evidence record](validation-results.md).

## Telemetry definition

One fixed data definition uses `TITLE` (`STRING256`), followed by 12 `FLOAT64` values:

| SimVar | Units / interpretation |
| --- | --- |
| `PLANE LATITUDE`, `PLANE LONGITUDE` | degrees |
| `PLANE ALTITUDE` | feet MSL |
| `PLANE ALT ABOVE GROUND` | feet AGL; nonfinite values become null |
| `AIRSPEED INDICATED`, `AIRSPEED TRUE`, `GROUND VELOCITY` | knots |
| `PLANE HEADING DEGREES TRUE`, `GPS GROUND TRUE TRACK` | degrees true, normalized 0–360 |
| `PLANE PITCH DEGREES`, `PLANE BANK DEGREES` | degrees; SDK sign converted to nose-up/right-bank positive display convention |
| `VERTICAL SPEED` | feet/minute |

`TITLE` is the loaded aircraft title; it is not assumed to be an ICAO model code. No reliable separate model identifier is currently exposed. Missing titles use an explicit fallback. No aircraft whitelist, aircraft-specific SDK definitions, fixed-wing speed minimum, or aircraft-file modification is involved. Invalid position/required numeric frames are dropped instead of recorded as plausible-looking data. Compatibility and sign/unit conventions must still be verified in actual fixed/rotary-wing flights.

The provider opens a local connection, subscribes to `SimStart`, `SimStop`, `Pause`, `AircraftLoaded`, `FlightLoaded`, requests the `Sim` system state and requests the user's aircraft at `SIMCONNECT_PERIOD_SECOND`. It waits for running state and a valid frame before publishing connected telemetry. Aircraft/flight transitions invalidate readiness. An eight-second stale-data timeout changes status to waiting, clearing live data; paused/loading periods are not filled with synthetic samples. Native calls and dispatch are serialized with a 50 ms asynchronous wait. Actual disconnections/quit/runtime errors are reported and retried after five seconds. Exceptions never cross the native callback boundary.

## Runtime and distribution

On a configured Windows x64 development PC, obtain the native x64 runtime **through the official SDK** and place it beside the published executable. Do not substitute the similarly named managed wrapper or an x86 FSX runtime. No SDK library is needed for synthetic mode on the published app.

This project does not include Microsoft's proprietary DLL, assume redistribution permission, or require end users to install a development SDK. Before a complete public release, the maintainer must review the installed SDK's redistribution/license terms and establish a permitted runtime-delivery method. The full-release packaging validator requires the runtime and explicit redistribution confirmation; the companion-only preview deliberately excludes it. If redistribution is not permitted, that is a full-release blocker that needs a supported Microsoft runtime installation route.

For the current review, `Test-SimConnectRuntime.ps1` checks a supplied SDK DLL's PE architecture/managed header and (on Windows x64) actual loading and all used native exports. Record its report along with `sdk-contract.log`; the DLL check is still pending on the gaming PC. The source ABI review uses 32-bit HRESULT/DWORD/flags/byte counts, x64 handles/pointers and serialized dispatch, and the C++ contract checks installed declarations/layouts before package building. Successful header compilation does not establish DLL loading or a working simulator connection.

If a DLL exists but its dependent Microsoft Visual C++ runtime is missing, loader diagnostics now distinguish that case from an absent `SimConnect.dll`. The Windows workbook provides the official Microsoft runtime guidance and exact local testing procedure.

`EnableToolbarBridge=false` disables the CommBus worker during focused telemetry diagnosis. Its default is true on Windows; failures are logged without stopping recording. The bridge requires the MSFS 2024 SU5+ CommBus API (public reference evidence identifies SDK 1.6.4+, to be confirmed against the installed SDK/runtime). An older runtime missing those exports is diagnosed separately from the basic telemetry connection.
