# Install AeroScience for Windows

For **v0.2.2**, follow [Windows build and acceptance](docs/v0.2.2-windows-testing.md). Testers download and run **AeroScience-Setup-v0.2.2.exe**. The internal candidate ZIP is not the recommended installation method. An older installed Community toolbar is detected at startup; close MSFS and choose Update Toolbar, or postpone and use Settings → Toolbar later.

The installer must be generated and accepted on Windows with the genuine MSFS 2024 SDK. No v0.2.2 setup EXE was generated or hardware-accepted in Linux. Preserve the tested candidate and finalize it without rebuilding.

The recommended public download is **AeroScience-Setup-v0.2.2.exe** from [GitHub Releases](https://github.com/JustSeanC/AeroScience/releases). The new v0.2.2 source requires its own Windows/SDK build, installation, upgrade and live science acceptance before publication. Prior v0.1.1 passes do not verify this changed release.

## Install and launch

1. Download the setup EXE under the release's Assets. The `.sha256` sidecar and `.release.json` record identify the exact tested installer. GitHub's Source code downloads are for developers. A portable ZIP, if present, is secondary.
2. Run the setup EXE. Review Welcome, version, notices and license, then choose an application folder. The default is `%LOCALAPPDATA%\Programs\AeroScience`, separate from your data. Setup shows installation progress, creates a Start menu shortcut and offers a desktop shortcut. It registers AeroScience in Windows Installed Apps.
3. The self-contained .NET application, authorized native SimConnect.dll, genuine SDK-built toolbar and signed Microsoft Visual C++ x64 prerequisite are bundled. If the shared prerequisite needs installation, Windows may ask for administrator approval. No SDK, Python, PowerShell, terminal, manually copied DLL or server startup is required. An installation error stops setup; do not replace SDK files yourself.
4. Leave **Launch AeroScience and finish first-run setup** selected at Finish. You can also open **Start → AeroScience** later. No extraction or search for AeroScience.exe is needed.

The setup EXE currently has no project Authenticode signing certificate configured; its release checksum identifies the tested bytes. The bundled Microsoft prerequisite is separately signature-checked. Any future installer signing must happen before recording its test hash.

## Guided first-run setup

The four-step AeroScience wizard opens automatically until completed:

- **Welcome:** explains local operation and preservation of existing settings.
- **Storage:** choose the Mission Database folder (the file is `missions.db`) and independently choose the Scientific Data Library folder, with native Browse buttons, default suggestions and available drive capacity. Defaults are `%LOCALAPPDATA%\AeroScience\missions.db` and `%LOCALAPPDATA%\AeroScience\ScienceLibrary`. Separate internal/external drives are supported; network/junction storage is not supported in this alpha. Future scientific datasets may be very large; no instrument-specific size forecasts are invented.
- **Toolbar:** review the Community folder discovered from MSFS 2024 Steam/Microsoft Store configuration, including custom packages paths. Browse if necessary. Approve Install / Update or select **Skip for Now**. Close MSFS before allowing Community changes. Previous AeroScience files are backed up outside Community; unrelated add-ons are retained. Known duplicate copies block installation with an explanation.
- **Review:** **Finish Setup** confirms the selected operations. Changed storage uses the existing verified SQLite backup/library migration services, keeps originals and never overwrites a nonempty destination. To open another existing archive/library, use Settings → Storage and explicitly confirm that switch. Setup does not silently adopt or merge it.

Existing locations are preselected, including v0.1.0 history. Cancelling the wizard does not silently finish with other choices. Once setup finishes, local services start and the browser dashboard opens automatically. The main window and dashboard show waiting/connected status; synthetic flight data is not substituted.

Settings → General & Toolbar → **Run guided setup…** revisits onboarding. Settings → Storage retains independent Browse, Open Folder, Restore Default, capacity and safe migration controls. End recording before migration. If an external data drive is unavailable, reconnect it or explicitly choose a verified store; the app does not create a replacement archive. An available mission archive remains independent of an unavailable Science Library.

## Record and review

Start MSFS 2024 and load an aircraft. After fresh live telemetry appears, choose Universal or Realistic, name and start a mission, then end it and open Mission History. Both modes currently record identically; instrument-specific Realistic features are future work. Mach is the simulator-provided value; unavailable historical Mach remains empty.

Leave AeroScience running while recording. Closing a browser or toolbar does not stop recording. Mission History, details and **Export complete mission (JSON)** remain available with MSFS closed and the PC offline. Exports retain metadata, timestamps, mode, source and every saved telemetry sample. No account or cloud service is required.

For optional iPad use, enable private-LAN access in Settings and choose the PC's private IPv4 address. Pair from the address/code displayed in the PC dashboard Settings. Local-only operation requires no firewall change. Physical iPad and VR results must be reported separately.

## Update and uninstall

End recording and exit AeroScience, then run the next version's setup EXE. The stable installer identity reuses the installation folder and shortcuts; existing data locations/settings remain selected. Data lives outside replaceable application files. If the toolbar needs updating, use Settings with MSFS closed. Startup automatically compares toolbar package versions and offers a confirmed graphical update. Remind Me Later postpones the prompt for that session; Settings → Toolbar can update/repair later. No automatic internet binary download is introduced.

To uninstall, close AeroScience and use **Windows Settings → Apps → Installed apps → AeroScience → Uninstall**. Application files and shortcuts are removed. Mission history, science files, settings and migration backups are retained by default. A separate **Yes/No** prompt asks about the Community toolbar; **No** keeps it. **Yes** moves only the recognized AeroScience package to a backup outside Community. MSFS must be closed. If removal fails or MSFS is running, the toolbar remains and a message explains it; reinstall AeroScience to manage it through Settings later. Other add-ons and the shared Visual C++ runtime remain installed.

Reinstalling the app reuses retained settings and available archives. Deleting user data is not an uninstall option in this alpha.

For problems, use **Collect Feedback**, save and review the local ZIP, and report the error/reproduction steps at [GitHub Issues](https://github.com/JustSeanC/AeroScience/issues). Installer logs are `Setup Log ... .txt` in `%TEMP%`; review paths/private details before sharing. Do not attach mission databases or authorization correspondence by default.
