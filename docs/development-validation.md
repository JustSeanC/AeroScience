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
