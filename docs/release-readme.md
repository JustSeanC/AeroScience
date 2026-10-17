# AeroScience — Live Flight Recording Alpha

AeroScience is an MSFS 2024 airborne science simulation and flight research recording application. This early alpha provides live aircraft telemetry, Mach, Universal/Realistic mission modes, recording/history, offline archive access, JSON export, a browser dashboard and an MSFS toolbar. Scientific instruments, mapping and research mission planning are planned for later versions.

1. Extract the entire Windows ZIP to a new writable folder.
2. Double-click **AeroScience.exe**. Its dashboard opens automatically.
3. With MSFS closed, open **Settings → General & Toolbar**, select the simulator's Community folder and choose **Install / Update Toolbar**.
4. Start MSFS, load a flight and open AeroScience from the simulator toolbar.
5. When fresh live aircraft data appears, name and start a mission. End it and inspect Mission History.

No SDK, developer tools, PowerShell, separate toolbar download, manual DLL copying or manually started server is required by the completed distribution. If Windows reports a missing Visual C++ dependency, use the **Microsoft Visual C++ prerequisite…** button in Settings, install Microsoft's x64 redistributable and restart the app. See INSTALL.md.

History works without MSFS or internet while its selected data folder is available. Settings → Storage independently controls the Mission Database and Scientific Data Library. Existing data remains outside this application folder. End recording before migrating storage; verified copies retain originals. Do not delete your data folders when updating the app.

This is an early alpha. See ACCEPTANCE.json and the release page for the exact tested build and remaining device/aircraft limitations. Both mission modes currently record identically; Realistic instrument limits are future work. The Science Library contains storage structure, not generated science products.

For problems, choose **Collect Feedback**, add reproduction steps, save the ZIP and review it before sharing. Report issues at https://github.com/JustSeanC/AeroScience/issues. Do not share private observations or mission databases unless you intend to.
