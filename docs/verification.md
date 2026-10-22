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

### Current P19 acceptance

Use the [private validation build guide](v0.5.0-private-validation.md) and
[A–K owner workbook](v0.5.0-acceptance-workbook.md). The consolidated
[readiness report](v0.5.0-readiness.md) distinguishes final-source technical CI,
fresh genuine public-provider/CMR checks, protected NASA originals and physical tests.
The 0.5.0 implementation is integrated into main; acceptance corrections are reviewed on
`release/v0.5.0-acceptance`. Public release and personal Earthdata login are separate
owner actions. The private profile preserves 0.4.2 on disk and
rejects production data paths. A private build is not an accepted production installer.

The former P17/P18 handoff remains in Git history; its test obligations are carried into
the workbook without claiming execution. Unexecuted hardware checks remain Not Tested.

- [Scientific methods](v0.4.0-scientific-methods.md) and retained [research records](research/README.md)
  distinguish genuine provider/numeric evidence from synthetic aircraft/HTTP fixtures.
  `scripts/release/verify_scientific_acceptance.py` requires current source-bound evidence.
  Current genuine provider/workspace/guided records and their execution-time source
  receipts pass; see the readiness report. Never edit hashes to relabel a historical pass.
  [Frozen-source rerun instructions](development-validation.md#frozen-source-scientific-acceptance)
  retain failed runs and independently validate originals before recording evidence.
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
