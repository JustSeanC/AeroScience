# MSFS 2024 SDK research for v0.2.0

Official documentation reviewed during source implementation on 2026-10-17. Documentation verification establishes names, units and documented semantics, not successful live reception in a particular aircraft. New Windows/SDK ABI compilation and live comparisons remain pending.

## Atmospheric suite

Source: [Miscellaneous SimVars, Environment](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimVars/Miscellaneous_Variables.htm).

| SDK variable | Requested unit | Stored/displayed unit | Meaning |
| --- | --- | --- | --- |
| `AMBIENT TEMPERATURE` | `celsius` | `degC` | Simulator ambient temperature |
| `AMBIENT PRESSURE` | `inHg` | `inHg` | Ambient pressure, without substituting a sea-level setting |
| `AMBIENT WIND VELOCITY` | `knots` | `kt` | Simulator ambient wind speed |
| `AMBIENT WIND DIRECTION` | `degrees` | `degT` | Wind direction relative to true north |
| `AMBIENT DENSITY` | `slugs per cubic feet` | `slug/ft3` | Simulator ambient air density |

These environment variables describe the **user aircraft location**, regardless of the selected aircraft/camera view. They are not spatial weather grids or remote sensing. Wind direction is described relative to true north; the app does not invent a wind-from/to convention beyond the documented value. Pressure/density stay in their documented native units. No humidity, cloud, visibility, dew-point or precipitation values are invented. No derived/model-based instrument is emitted in this release. Live Weather does not turn these readings into independent calibrated observations.

The auxiliary atmospheric definition is five FLOAT64 values, 40 bytes, separate from the existing aircraft definition. Nonfinite or physically invalid fields become unavailable individually; the others remain usable. If the SDK rejects an auxiliary definition, its channels become unavailable and aircraft recording continues. The current atmospheric definition is a single group: a definition-level rejection disables that group rather than claiming partial native success. Missing values never become zero measurements.

## Read-only guidance

Source: [Aircraft Autopilot/Assistant SimVars](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimVars/Aircraft_SimVars/Aircraft_AutopilotAssistant_Variables.htm).

All flags below are requested as FLOAT64 in `bool` units and decoded only for exact 0/1 values:

| Display | Actual indicators used |
| --- | --- |
| AP engagement | `AUTOPILOT AVAILABLE`, `AUTOPILOT MASTER` |
| Lateral HDG / NAV / ROLL | `AUTOPILOT HEADING LOCK`, `AUTOPILOT NAV1 LOCK`, `AUTOPILOT WING LEVELER`, `AUTOPILOT BANK HOLD` |
| LOC classification of captured NAV approach | NAV plus `AUTOPILOT APPROACH HOLD`, `AUTOPILOT APPROACH CAPTURED`, `AUTOPILOT APPROACH IS LOCALIZER` |
| Vertical ALT / VS / FLC / GS / PITCH | `AUTOPILOT ALTITUDE LOCK`, `AUTOPILOT VERTICAL HOLD`, `AUTOPILOT FLIGHT LEVEL CHANGE`, `AUTOPILOT GLIDESLOPE ACTIVE`, `AUTOPILOT PITCH HOLD` |
| Independent armed indications | `AUTOPILOT ALTITUDE ARM`, `AUTOPILOT GLIDESLOPE ARM` |
| Speed IAS / MACH | `AUTOPILOT AIRSPEED HOLD`, `AUTOPILOT MACH HOLD` |

Selected heading uses `AUTOPILOT HEADING SLOT INDEX` (`number`) with `AUTOPILOT HEADING LOCK DIR:0` through `:3` (`degrees`). Selected altitude similarly uses `AUTOPILOT ALTITUDE SLOT INDEX` and `AUTOPILOT ALTITUDE LOCK VAR:0` through `:3` (`feet`). Invalid/missing slot indices yield unavailable targets. VS uses `AUTOPILOT VERTICAL HOLD VAR` (`feet per minute`); IAS uses `AUTOPILOT AIRSPEED HOLD VAR` (`knots`); selected Mach uses `AUTOPILOT MACH HOLD VAR` (`number`). These are selected targets associated with explicit active flags, not measurements of achieved flight performance. Heading target is labeled `deg`, without assuming a true-north datum the SDK does not establish here.

The separate guidance definition contains 31 FLOAT64s / 248 bytes. Contradictory active flags or invalid indicators yield an unavailable category instead of choosing one arbitrarily. Selected targets alone never imply engagement. AP unavailable/disengaged does not imply a lateral or vertical active mode. GS armed is labeled **SDK arm indication** and never substituted for GS active. Approach/LOC classification requires explicit supporting flags, not merely being on an approach flight plan.

Custom avionics may not publish standard flags or may publish conflicting ones. The UI reports standard-variable status with that limitation. It cannot establish proprietary FMA truth without a documented aircraft-specific interface. No FD, yaw-damper, autothrottle or autopilot control feature is included. Mode changes are deduplicated; target adjustments do not spam events. Loading, aircraft change, pause, unavailable and disconnected states reset the change baseline; stale packets do not create false transitions.

## Preserved native contracts

The original aircraft data layout remains **360 bytes**, TITLE 256 bytes plus thirteen FLOAT64s. Actual aircraft Mach remains `AIRSPEED MACH` / `mach`, offset **352**; it is never calculated from IAS. Legacy samples may have null Mach. The SIMOBJECT data payload begins at byte **40**; CommBus DWORD counts/flags and offsets **20/24/28/32** remain unchanged.

[SimConnect_GetLastSentPacketID](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Debug/SimConnect_GetLastSentPacketID.htm) returns the last request's DWORD packet ID. It is used to attribute asynchronous SDK exceptions to the independent auxiliary definitions. The required export is now included in runtime checks. `sdk-contract.cpp` compiles this signature and size/offset assumptions against the installed official SDK; it must still run on Windows. Managed unit tests verify each guidance field offset, decoders, invalid values and preserved core layout. They do not substitute for the official-header build or live reception.

## Optional flight-plan investigation

[SimConnect_RequestSystemState](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/General/SimConnect_RequestSystemState.htm) documents `FlightPlan` as a file-path state, not a universal ordered route/waypoint feed. This does not establish reliable read access to every streamed/custom EFB flight plan in MSFS 2024. The current release therefore implements the complete manual planner and leaves automatic active-plan import unavailable, rather than offering an unreliable import control or changing the simulator plan. It is optional in the requested scope and does not block manual operations. No simulator flight-plan/control write API is called.
