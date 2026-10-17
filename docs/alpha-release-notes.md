# AeroScience v0.1.1-alpha — Live Flight Recording Alpha

This alpha uses live MSFS 2024 aircraft telemetry and a single Windows application entry point, AeroScience.exe. The local mission archive can be opened without MSFS or internet access.

- Added simulator-provided Mach to the dashboard, toolbar and stored/exported samples, with unavailable values retained as null.
- Corrected the recording interval gate that could reject alternating jittered 1 Hz observations. The dashboard separates configured target, sample cadence and mission-wide average.
- Added complete JSON mission exports with metadata, modes, events, source labels and dataset references.
- Added independent mission-database and scientific-library storage settings, folder selection, capacity information and verified migrations retaining originals.
- Added graphical status, connection/LAN settings, toolbar installation and local feedback export. No end-user terminal commands are required.
- Production generated telemetry and demo launchers are removed. Existing simulated recordings remain readable and labeled.

This document describes implemented behavior. A published installation-ready archive additionally requires the version-matched acceptance record and SDK receipt included in the package. See ACCEPTANCE.json for actual hardware evidence, remaining limitations and the tested configuration. Never infer live Mach, Windows GUI, external-drive, Quest 3 or physical-iPad acceptance from Linux tests.

Scientific instruments, remote sensing, replay, cloud synchronization and automatic application updating remain future work.
