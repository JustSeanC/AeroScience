# AeroScience documentation

For private 0.5.0 testing while 0.4.2 QA continues: [build and isolation](v0.5.0-private-validation.md),
[owner acceptance workbook](v0.5.0-acceptance-workbook.md), [readiness and blockers](v0.5.0-readiness.md).
No public 0.5.0 release is authorized.


Development additions: [instrument calibration and provenance](instrument-calibration.md)
describes P03; use the [development tracker](roadmap/v0.5.0-development-tracker.md) for
implementation and outstanding acceptance. Stable 0.4.2 QA material remains below.

Stable software and active external QA use **v0.4.2**. Version **0.5.0 is in development**
on `develop/v0.5.0`; planned contracts do not establish implemented or accepted features.

## Install, fly and review

- [Windows installation and onboarding](../INSTALL.md): setup EXE, independent storage,
  Community toolbar, offline history, updates and uninstall.
- [Pilot walkthrough](v0.4.2-pilot-guide.md) and [Mission Planner](mission-planner.md).
- [Scientific operations](science-operations.md): measurements, preparation, provenance,
  independent recording/collection, review and export.
- [Automatic remote-sensing quick start](remote-sensing/quick-start.md),
  [source availability and limits](v0.4.0-scientific-source-expansion.md),
  [troubleshooting](remote-sensing/troubleshooting.md), and
  [advanced manual import](remote-sensing/sources.md).
- [External Data Manager — find, approve, download, manage and reuse](scientific-library.md) (v0.5.0 development).
- [Storage, capacity and safe cleanup](remote-sensing/storage.md).

## Develop, verify and release

- [Current architecture](architecture.md) and [development/verification workflow](verification.md) and [two-tier CI](development-validation.md).
- [Windows build, installed acceptance and manual publication](public-release-windows.md).
- [v0.4.2 QA checklist](v0.4.2-windows-testing.md),
  [completion and limitations](v0.4.2-completion.md),
  [accessibility evidence](v0.4.2-accessibility.md) and
  [scientific visualization](v0.4.2-science-visualization.md).
- [Native SimConnect](simconnect.md), [SDK toolbar](toolbar.md),
  [window debugging](toolbar-interactions.md), and [SDK investigation](sdk-research-v0.2.0.md).
- [Runtime redistribution](runtime-distribution.md), [notices](../THIRD-PARTY-NOTICES.md)
  and [release history](../CHANGELOG.md).

## Scientific methods and retained evidence

- [Instrument reference profiles](v0.4.0-instrument-profiles.md),
  [scientific methods](v0.4.0-scientific-methods.md),
  [replay/coverage](v0.4.0-replay-and-coverage.md),
  [composites](v0.4.0-scientific-composites.md) and
  [comparison constraints](v0.4.1-scientific-comparison.md).
- [Research/verification records](research/README.md): original versioned JSON retains source
  identities, hashes, numeric checks and its original acceptance limitations. Older dates
  do not make this evidence disposable. These records are distinct from current CI runs.
- Active v0.4.2 QA files and the v0.4.1 visual/review baseline stay at their original paths.
  Earlier handoffs and superseded workbooks remain in Git history; the changelog keeps
  immutable links where historical detail matters.

## Active v0.5.0 development

- [Implementation tracker and resume point](roadmap/v0.5.0-development-tracker.md).
- [Approved scope](roadmap/v0.5.0-scope.md), [architecture](roadmap/v0.5.0-architecture.md),
  [dependency plan](roadmap/v0.5.0-implementation-plan.md),
  [acceptance criteria](roadmap/v0.5.0-acceptance.md),
  [decisions](roadmap/v0.5.0-decisions.md) and [backlog](roadmap/v0.5.0-backlog.md).
- [Documentation audit/disposition](roadmap/v0.5.0-documentation-audit.md) and
  [approved branding strategy](roadmap/v0.5.0-branding.md).

## Community & Support

Need help with AeroScience? Join our Discord community for support, troubleshooting,
questions, feature requests, and discussions with other pilots.
[Join our Discord](https://discord.gg/mpYyazCqvJ). Membership is optional. Existing
[GitHub issue reporting](https://github.com/JustSeanC/AeroScience/issues) and application
feedback/log collection remain available. Review private flight locations and credentials
before sharing diagnostic files.
