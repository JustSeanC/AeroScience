# AeroScience documentation

## Current release and source baseline

**Current owner-accepted baseline:** [AeroScience v0.8.0 Beta 1](https://github.com/JustSeanC/AeroScience/releases/tag/v0.8.0-beta.1), published November 22, 2026, as a regular GitHub release. Its installed version remains `0.8.0-beta.1`, with a separate beta profile and toolbar. No distinct `v0.8.0` stable-channel installer or automatic migration is implied.

**Active development branch:** [`main`](https://github.com/JustSeanC/AeroScience/tree/main). The accepted runtime was built from `c40db9311c9359b124f2373a2205a3708aecad58`, verified by [Full CI 44015112685](https://github.com/JustSeanC/AeroScience/actions/runs/44015112685) and owner-observed Windows/MSFS acceptance. The [Beta 1 owner handoff](roadmap/v0.8.0-p08d-handoff.md), [installation and acceptance guide](roadmap/v0.8.0-p08d-beta-acceptance.md), [tester guide](beta/v0.8.0-beta.1/START-HERE.html), [known issues](beta/v0.8.0-beta.1/KNOWN-ISSUES.md) and [QA cards](beta/v0.8.0-beta.1/TEST-CARDS.md) document that scope.

Older per-version scope, architecture, handoff, and scientific validation reports remain **historical records**, even when their original text describes a release as pending. Retired v0.7.1 and campaign-research branch tips remain reachable through verified archival tags under `archive/pre-v080-cleanup/`. Do not overwrite source receipts, alter scientific evidence, or treat earlier experimental plans as certified capabilities.

**Future scope:** [v0.9.0 airborne science and environmental missions](roadmap/v0.9.0-scope.md) is a proposal, not an implemented release.

**v0.8.1 development:** [X-Plane 12 read-only compatibility scope](roadmap/v0.8.1-scope.md), [architecture audit/design](roadmap/v0.8.1-architecture.md), [official API and dataref research](research/v0.8.1-xplane-feasibility.md), [P01–P06 implementation plan](roadmap/v0.8.1-implementation-plan.md), [P02 telemetry adapter and test handoff](roadmap/v0.8.1-p02-handoff.md) and [actual progress](roadmap/v0.8.1-development-tracker.md). P01 is merged; P02 is a separately tested candidate library, with production X-Plane registration/recording/science disabled. Genuine X-Plane acceptance remains Not Tested; no native X-Plane panel or v0.8.1 installer is included. The existing v0.8.0 Beta 1/MSFS installation remains the accepted release.

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
- [Windows candidate build and installed acceptance](public-release-windows.md):
  historical 0.5.0 and 0.4.2 handoffs; use the version-matched v0.8.0 Beta 1 guide above for the accepted installer.
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

## Historical v0.5.0 development

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

- [Organization Discord notifications](v0.7.0-organization-notifications.md) — optional external consent, administrator settings and delivery states.

## v0.7.0 integrated acceptance

[P28 engineering evidence and remaining release gates](validation/v0.7.0-p28.md) distinguish automated integration from installed MSFS, Discord, Railway and owner acceptance. No owner action is requested while hosting trust and first-owner delivery remain blocked. The linked short tester pages contain app-only checks for later acceptance.
