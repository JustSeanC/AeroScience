# Public Windows release preparation

Use the current [v0.4.0 Windows build, installed testing and publication guide](v0.4.0-windows-testing.md). Build locally with the official MSFS 2024 SDK, existing hash-bound runtime authorization and Inno Setup; no self-hosted runner is required.

The current scientific release stops at a genuine SDK-built installer candidate. Actual installed missions A–E, archive/upgrade/storage/installer and relevant device checks must be recorded for those exact bytes. Use the graphical `Complete-ReleaseAcceptance.ps1` checklist, then finalize with `New-Release.ps1 -AcceptanceRecord`. It preserves the tested EXE and produces its checksum/public evidence. Ordinary pilots only run the setup EXE and launch AeroScience normally.

Upload the finalized setup EXE, checksum and sanitized release evidence manually through GitHub Releases, selecting the exact tested source and prerelease status. The portable ZIP is optional. Older version-specific automatic-alpha procedures remain historical documentation for their own artifacts; they do not establish v0.4.0 hardware acceptance.

Release notes include the optional [official Discord community](https://discord.gg/mpYyazCqvJ) alongside existing feedback and GitHub support. No account or membership is required to use the application.
