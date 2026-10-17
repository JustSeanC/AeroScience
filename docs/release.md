# Release installation and provenance

The primary asset is **AeroScience-Setup-v0.1.1-alpha.exe**. Follow the [Windows build/test/manual publication guide](public-release-windows.md). No self-hosted runner is required for the first public prerelease. The portable ZIP is optional and secondary.

## Pipeline

1. `Build-ReleaseCandidate.ps1` performs the existing locked application/helper/recovery tests, Windows publish, official installed-header ABI checks, genuine SDK compilation/validation, native runtime loading/export checks and private-safe public permission attestation validation.
2. `Build-Installer.ps1` stages that validated app/toolbar/runtime payload, verifies the full Microsoft Visual C++ x64 prerequisite's signature and identity, invokes an installed official Inno Setup 6.3+ compiler and records hashes of the EXE, payload files, compiler, recipe and prerequisite. No SPB or SDK manifest is synthesized.
3. Test the exact setup EXE on Windows. `installer-candidate/ACCEPTANCE.json` binds source, payload, setup SHA-256 and build-receipt SHA-256. Eleven minimum checks include fresh install, wizard, upgrade and uninstall preservation. Prior ZIP/simulator evidence cannot count for changed installer bytes.
4. `New-Release.ps1` calls `installer_release.py finalize`. It validates actual acceptance and copies the identical EXE to the final output; it does not recompile, sign or alter it. It writes `.exe.sha256`, `.release.json`, generated notes and an internally verified optional portable ZIP. An unchanged EXE is essential because its tested hash is the release identity.
5. Manually tag the tested commit, upload the final EXE/checksum/public evidence on GitHub Releases, mark prerelease and verify signed-out downloads. Optional CLI automation applies identical validation and refuses conflicting tags/assets. Original private correspondence and personal archives/logs are excluded.

The `.release.json` contains the compiler receipt and recorded acceptance. The installer embeds operational files and public runtime/SDK evidence; it does not embed a pending candidate marker or fake a final acceptance result before testing. Final portable archives include the real completed acceptance. The internal ZIP is retained for final payload/SDK/runtime revalidation even if it is not uploaded.

## Installed behavior

Inno uses a fixed AppId for future in-place updates, per-user installation, Start/optional desktop shortcuts, Installed Apps registration, a normal uninstaller and a shared desktop mutex to require the app be closed. The default application directory is `%LOCALAPPDATA%\Programs\AeroScience`; the default data/configuration directory remains `%LOCALAPPDATA%\AeroScience`. Setup does not reset settings or create/delete mission records.

The graphical four-step app wizard handles storage and Community integration through the same verified services as Settings. Existing paths are preselected. Location changes are explicit verified copies with retained originals. Community discovery reads 2024 Steam/Store configuration and custom packages roots; confirmation/Browse/Skip remain available. MSFS-running checks, SDK hashes, duplicate detection and safe package backups apply to installation and updates. Uninstall separately asks about removing only AeroScience from Community; files move to a backup rather than deleting unrelated add-ons. User databases, libraries, configuration and shared Visual C++ dependencies remain.

## Acceptance and limits

The minimum alpha checks are Windows app/dashboard startup, offline archive/export, toolbar install/load, fresh live SimConnect, completed/restarted mission history, dependencies, installed payload integrity, installer installation, setup wizard, upgrade preservation and uninstall preservation. Extended live recovery, migration edge cases, Mach/cadence/aircraft, iPad and VR may remain explicitly pending for the alpha; stable gates retain extended checks.

Windows hosted CI can compile the recipe using unmistakable structural fixtures without the proprietary SDK/runtime. Such compilation is not installer execution, authentic SDK output, licensing evidence, Windows acceptance or a distributable release. This Linux cloud can cross-publish Windows source but cannot execute the Windows installer or MSFS.

Inno Setup and Microsoft runtime licenses remain applicable; see [third-party notices](../THIRD-PARTY-NOTICES.md). The approved SimConnect attestation remains hash-bound and original correspondence stays private. No project signing certificate is configured. Any future signing must precede receipts and acceptance. Versions, setup filenames, source tags and prerelease status derive from `Directory.Build.props`; the numeric SDK package version must match. Never move a published tag or overwrite an existing release.
