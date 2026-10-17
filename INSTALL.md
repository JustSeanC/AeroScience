# Install and use AeroScience

The v0.1.1 graphical app is implemented and cross-published, but a complete friends-only alpha still needs official SDK output, approved runtime distribution and Windows/hardware verification. These are the intended user steps for that complete download. Old v0.1.0 console previews and GitHub source ZIPs are different artifacts.

## First launch

1. Download the approved alpha archive from the private tester location. Extract all files to a new writable application folder. Do not launch from inside the ZIP or merge files into an old preview.
2. Double-click **AeroScience.exe**. Keep its accompanying files together. No .NET SDK, MSFS SDK, Visual Studio, Python, PowerShell, batch launchers or manual localhost setup is required by the completed alpha.
3. The app initializes the local archive, starts its services, opens the dashboard and attempts to connect to MSFS. It can launch before the simulator. Review the Science Library capacity notice on first run.
4. Open **Settings → General & Toolbar**. Verify the Community folder, using Browse if automatic discovery is unavailable. With MSFS closed, choose **Install / Update Toolbar** and confirm. Existing AeroScience packages are backed up outside Community; unrelated add-ons are preserved. A complete alpha includes a genuine ready-built package. A developer preview may not include one and will report that clearly.
5. Start MSFS, load a cockpit and open AeroScience from the simulator toolbar. Require connected status, the loaded aircraft and fresh flight values before recording. Mach uses the simulator's value; missing Mach shows unavailable.

The developer preview does not bundle a proprietary SimConnect runtime. Its graphical official-runtime import is for personal testing using an official existing installation. The licensed end-user runtime/prerequisite route must be resolved before calling the full alpha installation-ready; testers must not be directed to build tools or manual DLL-copy commands.

## Record a flight

Choose Universal or Realistic, enter a mission name and start from the toolbar or dashboard. Universal is the default; advanced Realistic instrument features are not implemented. Both record live MSFS telemetry. Generated demo data is removed.

Leave AeroScience running while recording. Closing a browser/toolbar does not stop the mission. End from either display, then open Mission History. **Export complete mission (JSON)** downloads all recorded samples, metadata, timestamps, mode, events, source labels and dataset references without changing the saved mission.

The rate display separates configured target, sample cadence and mission-wide average. Paused/unavailable data is not replaced with invented samples. An interrupted mission retains committed data. Exiting the app while recording asks for confirmation.

## Open history offline

Launch AeroScience.exe with MSFS closed, including with no internet connection. Open Mission History, its details and exports normally. Live telemetry remains waiting/unavailable, and the app reconnects when MSFS starts. There is no account, mandatory cloud service or internet dependency for the archive.

## Storage

Under **Settings → Storage**, Mission Database and Scientific Data Library have independent paths, Browse/Change, Open Folder, Restore Default, usage and free-space displays. They may be on different internal/external drives.

Defaults are `%LOCALAPPDATA%\AeroScience` and its `ScienceLibrary` subfolder. The preferences file stays in the fixed local AeroScience directory. Existing v0.1.0 missions, Universal/Realistic modes and historical simulated source labels are retained.

End recording before changing either location. Confirm copying/switching in the dialog. Services stop, the SQLite backup or library copy is verified, settings switch only after verification, and originals remain intact. No automatic merge/overwrite/cleanup occurs. If a destination already contains a store, explicitly open that existing store or choose another empty folder. Restoring Default can select an older retained copy; the dialog explains the choice and does not merge newer records.

Unavailable external storage produces an error rather than a fresh empty archive. Reconnect it or explicitly select an existing verified store. An unavailable Science Library does not erase the independently available mission archive. Unverified network/junction storage is unsupported. Do not select the application installation folder for data.

Schema upgrades create verified backups in the mission folder's `Backups` directory. Use the original backup for rolling back to v0.1.0; its old executable refuses schema 3. Details and interruption recovery are in [the alpha guide](docs/alpha-testing.md).

## Optional iPad access

In Settings, enable private-LAN access, choose the PC's private IPv4 address and Apply with no recording active. Allow the app on the Windows **Private** profile only if needed. Local-only use needs no firewall changes.

On the PC dashboard, Settings shows the LAN address and pairing code. Open that address in iPad Safari and enter the code. Codes change on app restart and sessions expire. A remote display cannot retrieve the pairing code. Use only a trusted private LAN; do not forward the service through a router. Physical iPad acceptance is pending.

## Feedback, updating and removing the app

Use **Collect Feedback** in the app, add optional notes and choose a ZIP location. Review it before sharing. The collection is local and excludes mission databases, SDK DLLs and pairing secrets by default.

To update, end recording, exit AeroScience and extract the new app to a new folder. Preserve the selected data folders and fixed local settings. Use the graphical toolbar update with MSFS closed. No automatic updater is required.

To remove the app, exit it and remove its application folder and own Community package. Keep mission/science folders, verified backups and `%LOCALAPPDATA%\AeroScience` unless you explicitly choose to delete your data. The app never deletes missions/scientific products to reclaim space.
