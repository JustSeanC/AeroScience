# AeroScience toolbar source

This is an SDK project, not an installation-ready Community package. The official
Windows MSFS 2024 package builder must generate manifest, layout and compiled panel SPB.
No placeholder binary or fabricated SDK receipt is supplied.

Pilots use [the normal Windows installer](../INSTALL.md). Maintainers use the
[Windows SDK build/acceptance procedure](../docs/public-release-windows.md),
[toolbar implementation](../docs/toolbar.md) and
[native-window troubleshooting](../docs/toolbar-interactions.md).
`Build-Toolbar.ps1` builds the full toolbar by default; `-ProofOfConcept` and `-WindowProbe`
are explicit diagnostic builds, not release substitutes. The installed official SDK and
header ABI contract remain authoritative.

Mission / Payload / Map navigation and recording decisions use the central companion.
The panel loads native SDK templates and `/JS/dataStorage.js`; do not replace its window
handlers or remove that dependency. It uses bounded named CommBus requests/replies,
without new sockets, unsolicited reply backlogs or per-aircraft control scripts.

The offline map uses the same Natural Earth geometry/display helpers as the companion.
Route geometry and incremental track replies are bounded; the complete recording remains
in SQLite. Display reduction never changes exported route/scientific precision. Browser
fixtures exercise this interface but do not establish native loading, window interaction,
physical headset or simulator performance acceptance. Current installed testing remains
in the [v0.4.2 checklist](../docs/v0.4.2-windows-testing.md).

Version 0.5.0 native intelligence and approved branding integration are tracked separately
in the [development tracker](../docs/roadmap/v0.5.0-development-tracker.md). Persistence
contracts alone do not activate those controls. Do not publish a new package until its
genuine SDK build and separately required acceptance are complete.
