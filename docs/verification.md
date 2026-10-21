# Development verification and acceptance

The [development tracker](roadmap/v0.5.0-development-tracker.md) names the latest tested
source, actual counts and pending v0.5.0 work. The [v0.4.2 QA checklist](v0.4.2-windows-testing.md)
and [completion record](v0.4.2-completion.md) remain the external-testing baseline.
Do not reuse an old pass to approve changed source or installer bytes.

See [two-tier CI and development validation](development-validation.md) for routine targeted checks, full milestone/manual runs and failure policy.

## Local automated checks

Use the pinned SDK in `global.json`, the Node version required by `package.json`, Python 3
and Chromium. On Windows, use PowerShell 7 in the repository; an x64 Native Tools shell is
needed for the later genuine SDK build. `scripts/build/Build-Companion.ps1` runs locked
restore, .NET tests, offline asset integrity/source checks and self-contained Windows publish.
It does not execute MSFS or approve a release.

For individual checks:

```text
dotnet restore AeroScience.sln --locked-mode
dotnet test AeroScience.sln -c Release --no-restore
npm ci --ignore-scripts
npm run check:web
npm run test:web
python -m unittest discover -s tests -p "test_*.py"
dotnet build tests/AeroScience.TestHost -c Release --no-restore --output artifacts/recovery-test-host
python scripts/build/smoke_recovery.py --host-dll artifacts/recovery-test-host/AeroScience.TestHost.dll
python scripts/build/smoke_evidence_recovery.py --host-dll artifacts/recovery-test-host/AeroScience.TestHost.dll
```

The cloud workspace may source `scripts/env.sh` for its installed tools. Browser tests
start an isolated synthetic host; they do not establish genuine scientific observations
or native simulator rendering. Recovery checks kill actual test processes against temporary
archives. Logs/TRX/browser artifacts stay under ignored test/artifact directories.
CI retains diagnostics without process memory dumps. Skipped/failed/cancelled checks are
not passes; inspect Actions for the exact pushed commit.

## Scientific and hardware acceptance

### P17/P18 handoff for P19 — not executed in cloud development

Use the [existing Windows SDK/runtime/installer procedure](public-release-windows.md)
only when P19 is authorized and its source-bound scientific prerequisites are satisfied.
Record exact source and installer hashes, SDK/runtime versions, aircraft, devices and
results. Keep private Earthdata correspondence/credentials out of logs and exports.
Preserve the current 0.4.2 QA material and test only disposable archive copies.

1. Install/upgrade the exact candidate on the gaming PC; verify application version,
   existing storage/settings/history, offline report/replay/export and Library relocation.
2. In a continuous flight, arm science during transit; collect A, pause/resume A, End A,
   start B, End B, then separately End Flight. Check stable session IDs, retained values,
   timestamps, overall frozen obligations and independent session reports after restart.
3. Fly explicit Start/End point/gate triggers and optional survey-boundary pause/resume
   with every UI closed. Verify manual End suspends automatic starts until explicit rearm;
   final waypoint and polygon exit never infer End. Exercise duplicate crossings,
   disconnect/reconnect, stale positions, slew, pause and unexpected engine interruption.
4. Pair actual iPad Safari; operate the same flight from desktop/iPad/toolbar. Delay an A
   command until B exists and confirm rejection, with no new target. Check keyboard,
   touch, orientation, zoom, reduced motion, dialogs, focus, stale/reconnect and offline
   archive review. Browser emulation does not satisfy this device check.
5. Compile/install the genuine toolbar with the official MSFS 2024 SDK. Verify Orbital Arc
   icon, native Mission/Payload/Map navigation, plan selection, recording and session
   controls, CommBus synchronization, hiding/reopening and engine restart. Confirm no
   simulator/autopilot write controls. Test multilingual metadata without bridge failure.
6. In desktop MSFS and actual Quest 3 VR, move/resize/scroll the native panel and reach all
   controls at small sizes. Toggle Compact VR on every tab with unsaved inputs; verify
   tab, plan, flight and collection remain unchanged. Test actual simulator input focus.
7. Run a representative 30–60 minute multi-session mission: monitor recorder cadence,
   independent scientific acquisition, bounded live summaries and responsiveness. Review
   raw/source/derived distinctions, grades/uncertainty and score denominators after flight.
8. Separately complete user-authorized Earthdata browser access and independently verify
   genuine Classic and NG original numerical files, provenance and offline reuse. Both
   remain **Pending**; CMR discovery and synthetic cubes are not numerical acceptance.
   EMIT remains discovery/official access only. Do not solicit credentials for this task.

No P19 execution, distributable installer or public release is authorized by this handoff.

- [Scientific methods](v0.4.0-scientific-methods.md) and retained [research records](research/README.md)
  distinguish genuine provider/numeric evidence from synthetic aircraft/HTTP fixtures.
  `scripts/release/verify_scientific_acceptance.py` requires current source-bound evidence.
  The v0.5.0 records are pending until genuine final-source reruns; never edit hashes to
  relabel a historical pass.
- [Windows release procedure](public-release-windows.md) requires the official installed
  MSFS 2024 SDK, ABI/compiler/package validation, authorized exact native runtime and
  installer checks. Inno recipe CI fixtures are deliberately non-distributable.
- Actual Windows/MSFS, physical iPad Safari and Quest acceptance require those platforms.
  Record source/installer hash, aircraft, scenarios, results and limitations. Hosted Windows
  compilation, browser emulation and a package validator do not establish that acceptance.
- Preserve original archives and verified upgrade backups. Test migrations, interruption,
  missing drives, offline history/export, upgrade/uninstall preservation and data clearing
  on disposable copies. Never test deletion on the only research archive.

The early SDK/window investigation remains in [SimConnect](simconnect.md),
[toolbar debugging](toolbar-interactions.md) and immutable changelog links. Initial reported
panel movement/replies are historical findings, not current full-device acceptance.
