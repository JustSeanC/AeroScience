# P07 localization coverage matrix

Catalog **24**, **7,040 messages per locale**. Complete bundle identity: `bd208d571cdf0eee75c8b2b5a6cebe4e24cf1f86f94e4da4f821a05ae05c84fb`.

Each language cell shows **bundled referenced keys / explicit English fallback keys** for that surface. These are source references, including computed families; they are not a percentage of visible interface coverage. Retained proper names and scientific terms are distinct from English fallbacks.

| Surface | en-US | es-ES | fr-FR | de-DE | pt-BR |
| --- | ---: | ---: | ---: | ---: | ---: |
| Dashboard | 1632 / 0 | 1632 / 47 | 1632 / 80 | 1632 / 82 | 1632 / 40 |
| Missions | 1369 / 0 | 1369 / 28 | 1369 / 55 | 1369 / 61 | 1369 / 31 |
| Current Flight | 317 / 0 | 317 / 10 | 317 / 9 | 317 / 11 | 317 / 6 |
| Scientific Results | 880 / 0 | 880 / 19 | 880 / 40 | 880 / 37 | 880 / 18 |
| Library & Downloads | 691 / 0 | 691 / 10 | 691 / 15 | 691 / 21 | 691 / 11 |
| Organizations | 1663 / 0 | 1663 / 20 | 1663 / 46 | 1663 / 41 | 1663 / 23 |
| Native Toolbar | 283 / 0 | 283 / 12 | 283 / 15 | 283 / 10 | 283 / 6 |
| Windows Companion | 269 / 0 | 269 / 2 | 269 / 17 | 269 / 10 | 269 / 3 |
| Consent & Safety | 288 / 0 | 288 / 3 | 288 / 10 | 288 / 10 | 288 / 2 |
| Error & Recovery | 301 / 0 | 301 / 1 | 301 / 2 | 301 / 6 | 301 / 0 |
| Contextual Help | 233 / 0 | 233 / 9 | 233 / 10 | 233 / 7 | 233 / 5 |

The [detailed matrix](coverage-matrix.json) contains exact key lists and sampled bindings. Ten locale/theme sample sets exercise real browser controllers; shared chrome and mounted/cached views are included. The [labeled screenshot index](screenshots/README.md) has 41 synthetic Chromium captures. Neither measurement proves every rendered branch.

| Evidence category | Status |
| --- | --- |
| Catalog keys, placeholders, source/bundle identities, fallback and Unicode | Automated validators and focused tests passed; exact milestone CI recorded in the handoff. |
| Rendered browser/native fixtures | Focused labels, controls, responsive/accessibility and immutable export checks; all-five-language chart tests. |
| Fluent human review, es-ES/fr-FR/de-DE/pt-BR | **PENDING** for every candidate; no qualified review receipt supplied. |
| Installed Windows / WinForms / actual Windows assistive technology | **NOT TESTED**; resource/API tests and cross-compilation are separate evidence. |
| Installed MSFS 2024 / Coherent GT / real native Unicode and lifecycle | **NOT TESTED**; classic ES2017 and synthetic panels are separate evidence. |

The source audit retains **490 manual classifications** and **267 computed/unreferenced-key review items**. They are not credited as localized or rendered. Original source/evidence/mission text, identifiers, UTC, hashes, units and technical diagnostics can remain in their original language. See the [critical review index](critical-review-index.json) and [reviewer guide](translator-guide.md) before any localization release acceptance.

Milestone automation: [Full CI 43604189721](https://github.com/JustSeanC/AeroScience/actions/runs/43604189721) passed on `fef75b7bd0a56ab5a99c2ce1d4b9f7b66c0b98c7`. See the [exact-source receipt](p07-full-ci-receipt.json) for counts and existing skips. This result does not change the human-review, manual-audit or installed-acceptance categories above.
