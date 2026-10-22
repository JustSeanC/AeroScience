# Development and full integration validation

The `foundation` workflow has two scopes on `develop/v0.5.0`. Neither scope establishes
genuine scientific or Windows/MSFS/iPad/Quest acceptance. The release scripts retain
their complete checks, current scientific evidence requirements, official SDK/runtime
authorization and exact-installer acceptance gates.

## Routine development

Ordinary development pushes run the **development** job: locked restore, solution
compilation, conservative affected .NET tests, web syntax and pinned-asset checks,
dashboard smoke plus affected browser specs, and affected Python helpers. New/changed
tests are included. Unknown shared dependencies expand to the whole .NET suite; an
unavailable diff expands all test families. The selected scope is printed in the job
summary and retained as `development-plan.json` beside TRX/browser diagnostics.

This is a targeted result, not full integration acceptance. Batch logical changes;
roadmap-only tracker edits retain the existing workflow path exclusion. Do not start
another full suite merely to record a progress note.

## Completed milestones and integration

Run **full** at meaningful implementation milestones and before proposing integration
into main. Main pushes and PRs targeting main retain full validation. To request a
development milestone full run on push, include `[full-validation]` in its commit message.
Alternatively, use Actions → foundation → Run workflow, select `develop/v0.5.0`, and
choose `validation: full`. Maintainers with GitHub CLI can run:

```text
gh workflow run ci.yml --repo JustSeanC/AeroScience --ref develop/v0.5.0 -f validation=full
```

The workflow must be present on the selected ref. If GitHub's web UI does not expose
branch-only dispatch changes yet, use the CLI/API or the milestone push marker; do not
modify main just to enable the button. Full runs have a different concurrency group
from routine pushes, so unrelated development does not cancel a meaningful validation.
Continue independent work while they run; record the exact validated source.

Full retains all Linux .NET/browser/Python suites, real-process recovery and evidence
kill tests, Windows .NET/companion publishing, Windows-sensitive helper checks, and Inno
structural compilation. Scientific evidence verification tests remain in both full
platform jobs; genuine final-source reruns and the fail-closed release verifier remain
separate release prerequisites. A pending genuine report is not an acceptance pass.

Windows repeats .NET intentionally: file locking, SQLite durability/migration, platform
runtime and shutdown behavior need that platform. Windows helper selection omits only
the portable airport catalog parser and static toolbar-source suite; Linux full still
runs them. Git byte preservation, source-bound scientific checks, PowerShell, paths,
installer/payload/conflict checks and new unknown helper modules remain on Windows.
Browser execution stays on Linux; Windows source/asset checks also verify Windows
checkout bytes before publish. No tests or assertions have been deleted.

GitHub schedules execute the default-branch workflow. No misleading develop-only cron
or change to main is introduced here. Until integration, request a full run at least
weekly during active development and at completed milestones; avoid a second run if
the same source already has the required full result.

## Failure handling

For a validation-profile publishing change, a manual diagnostic run can check actual
Windows isolation/protocol tests, production and isolated `win-x64` publishing, icons
and unchanged dependency locks before the full milestone run:

```text
gh workflow run ci.yml --ref develop/v0.5.0 -f validation=windows-profile
```

This development-only diagnostic does not replace either validation tier or a full
milestone pass. Other branches still require full validation. Both the private build
and full Windows CI use `Publish-ValidationCompanion.ps1`: locked solution restore
without a global RID into isolated profile intermediates, then profile probe/publish
with `--no-restore`. This preserves runtime-neutral project locks, separate compiled
identities and the existing production companion restore/publish approach.

Windows full CI places temporary fixtures on the runner's dedicated scratch volume
(`RUNNER_TEMP/aeroscience-test-storage`) and logs a small, explicitly flushed write probe
for both the default system temp and selected scratch. SQLite FULL durability, scientific
queues, the ten-second drain, five-second per-packet progress and two-minute inactivity
collector remain unchanged. This isolates CI fixture storage from OS-disk contention;
it does not change application storage defaults or establish gaming-PC throughput.
The blocked-writer probe logs actual durable progress, including before a failed
per-packet assertion, so an inactivity-collector termination is distinguishable from
an unexplained native crash. Do not count an aborted test host as passing remaining tests.

Read the failing test, error, TRX/sequence and source delta first. Allow at most one
diagnostic rerun for a suspected intermittent failure. Recurrence requires a root-cause
investigation before further retries. There are no automatic test retries or
`continue-on-error` paths in this workflow.

Early Windows test-host abortion was the inactivity collector ending long export
fixtures, not evidence of a native runtime crash. Bulk fixture setup retained every row,
assertion and FULL durability. The API directory-lock race needed observing actual
lease disposal, beyond ApplicationStopped/provider-disposal initiation. P02's later
multispectral fixture hit its unchanged 10-second publication deadline while unrelated
SQLite/raster fixtures ran concurrently. It now shares the existing scientific-I/O
isolation collection and records import/Apply/publication timing plus worker state on
timeout. Explicit scientific-worker concurrency tests remain intact. Only a subsequent
passing full run can resolve that milestone's failed Windows result.

The first DOC-01 targeted combination also exposed a browser-fixture dependency: the
periodic synthetic provider could claim the first remote acquisition slot outside the
small raster's geographic coverage before the fixture's explicit packet arrived. Its
scoped test provider now holds the correct synthetic location, and failed fixtures close
their temporary flight in `finally`. The deadline, one-product assertion and scientific
checks are unchanged. This test-host correction is not a production science workaround.

Shared browser-host changes and previously unmapped browser modules/styles expand to the
whole browser family. The targeted tier still avoids the Windows/full integration jobs;
its reported scope may grow when a dependency is shared. Preserve Playwright JSON and
failure screenshots/context alongside TRX/sequence diagnostics so failures can be diagnosed
without immediately rerunning them.

The browser suite uses one authenticated seed session per local test-server run, saved
only in ignored `artifacts/browser-auth.json` and removed at teardown. Each browser/API
context still has isolated state and still performs the normal session/CSRF flow. This
prevents discarded test contexts from exhausting the unchanged production 128-session
limit during a complete suite. Do not upload the seed file or relax authentication to make
UI tests pass. Configuration/shared browser fixture changes select the full browser family.

## Geographic Scientific Library (P10–P12)

P19A full Windows CI also executes the unpublished test host's `--earthdata-vault-probe`
against the actual desktop credential-store source. It writes/reads/removes disposable
synthetic Windows credentials under two random configuration identities and checks that
disconnecting one cannot affect the other. It does not contact NASA or establish a real
Earthdata login, protected original download, native dialog or physical-device acceptance.

The full platform jobs also run `smoke_library_recovery.py --host-dll <test-host.dll>`:
two actual process kills retain approved intent, confirmed bytes and unresolved read
reservations without publishing a source or creating a mission. `library-restart.spec.js`
restarts an isolated real companion and verifies the resulting job through the browser.
Browser catalog/download fixtures replace only provider HTTP in the unpublished test host;
the production catalog, SQLite job store, priority scheduler, numeric reader and importer run.
Recorded Census geometry is a deterministic fixture, not a live-service acceptance claim.

For an explicitly authorized bounded genuine check, use a new empty output directory:

```text
dotnet run --project tests/AeroScience.ProviderAcceptance -- --network --library --output artifacts/library-genuine-check
python scripts/research/verify_library_sources.py artifacts/library-genuine-check
```

The second command requires development-only rasterio/numpy. Neither package is shipped or
required from pilots. The first command queries/downloads real public USGS, Copernicus,
Landsat and Sentinel-2 subsets, retrieves actual Census boundaries, follows a provider's
real pagination link, verifies retention/reuse and creates no flight/session. Provider
failure is a failed or missing genuine check; mocks never replace it. The independent check
retains strict numeric/hash/CRS/affine/QA assertions and the reader's documented boundary
snap. Hardware, installed Windows, physical paired devices and sustained operational
acceptance remain separate. Existing release gates are unchanged.

## Frozen-source scientific acceptance

For the full public-provider/workspace/guided-ENVI gate, commit the corrected source first
and use a **clean checkout** plus a new evidence directory outside the repository:

```text
python scripts/release/complete_scientific_acceptance.py --output <new-evidence-directory> --python <rasterio-python>
python scripts/release/verify_scientific_acceptance.py
```

Use a developer-only Python environment with `rasterio==1.4.3` and `numpy==2.2.6`.
The runner performs a locked restore, captures source identity before each execution,
runs actual four-provider acquisition/products, regional workspace, guided Cuprite import,
Library jobs/reuse and public CMR discovery, then independent numeric/QA/CRS comparisons.
`--cuprite-originals <folder>` may reuse the unchanged, pinned genuine DAT/HDR plus their
actual `download-receipt.json`; it still reruns import/sampling/offline/workspace checks.
Otherwise the existing bounded public tutorial downloader obtains them. No fixtures
replace failed provider data. Current per-download, storage and network limits still apply.

Raw outputs and originals remain outside Git. Completed `run-source.json` receipts seal
the run artifacts and all relevant canonical-LF source hashes. Recorders refuse missing,
unfinished, changed or stale runs and validate before atomically replacing canonical
records. A failed recorder leaves its previous record intact. Keep failed runs for diagnosis;
do not hand-edit status/hashes or restamp older results. A later evidence/documentation-only
commit may retain acceptance only when **every hashed scientific source file is unchanged**.
Run the aggregate verifier again on the exact final checkout.

Protected NASA files are separate. Follow [workbook Group J](v0.5.0-acceptance-workbook.md#j--pending-owner-authorized-earthdata-access-and-nasa-originals)
for authorized acquisition and independent owner-local Classic/NG comparisons. Public
CMR metadata and Cuprite cannot pass either family's original-file acceptance. Native EMIT
import remains unsupported. No acceptance runner finalizes an installer or publishes a release.
