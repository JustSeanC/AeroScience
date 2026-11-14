# P07 candidate translation and reviewer guide

These catalogs are **candidate translations**, prepared with automated offline drafts and explicit developer-authored terminology/safety overrides. No qualified fluent human review has been supplied. Passing tests is engineering evidence, not human language acceptance or installed Windows/MSFS acceptance.

## Resources and reproducible checks

The sole source is `src/AeroScience.Engine/wwwroot/i18n/locales/en-US.json`, with `message-schema.json` context, required parameters, surface, critical flag, source locations and code-point limit. Edit the peer `es-ES`, `fr-FR`, `de-DE` and `pt-BR` catalogs. Keep keys, original identifiers, number precision, literal units, hashes and retained UTC unchanged. Do not translate user names, source/product identities, ICAO codes or original evidence. Translate display explanations around those values.

Run `node scripts/build/build-locales.mjs --check`, `node --test tests/localization-audit.test.mjs tests/localization-runtime.test.mjs`, and `npm run test:presentation`. Regenerate with `node scripts/build/build-locales.mjs`. The optional `scripts/localization/draft-candidates.mjs` requires separately installed/pinned development tooling and public model files; the application never loads a model, requests a translation account or downloads a locale. `assemble-candidates.mjs` retains explicit pending English fallbacks and source fingerprints. Draft outputs require review before they become release resources.

## Review procedure

1. Review the glossary first, especially recording/collection and original/derived distinctions. Check the complete sentence in its documented view and authoritative state. Do not approve a menu-only sample as an entire locale.
2. Review every critical message: operations, stale/mismatched builds, consent, live location, uploads, source access, permanent deletion, authorization and recovery. Confirm negation, scope, recipient, irreversible effects and explicit action remain unambiguous.
3. Review zero/one/multiple and million counts. `one`, `other`, and approved `many` forms use nonnegative integer counts. Use parameterized complete messages, retaining each placeholder exactly. Decimal scientific values use value messages, not count pluralization.
4. Exercise the screenshot recipes in desktop/narrow, light/dark, 200% scaling and native minimum panel fixtures. Browser screenshots are labeled synthetic. Compare text with the exact source catalog identity. Never approve an installed native panel from Chromium captures alone.
5. Inspect fallback entries and the separate static/rendered coverage report. A missing, copied, stale or unsafe phrase remains English explicitly. Correct it with context; do not remove the fallback flag merely to improve a percentage.
6. Record reviewer name, relevant language competence/qualification, locale, catalog identity, reviewed keys/surfaces, date, issues, corrections and evidence paths. Partial review must identify its scope. Only documented qualified review of the current source may set the manifest review state to `reviewed`.

## Scientific and operational glossary

| Concept | es-ES | fr-FR | de-DE | pt-BR |
|---|---|---|---|---|
| Flight recording | Grabación del vuelo | Enregistrement du vol | Flugaufzeichnung | Gravação do voo |
| Scientific collection | Adquisición de datos científicos | Collecte scientifique | Wissenschaftliche Datenerfassung | Coleta de dados científicos |
| Armed, waiting for trigger | Preparada, en espera del desencadenante | Prête, en attente du déclencheur | Bereit, wartet auf Auslöser | Preparada, aguardando acionamento |
| Pause / resume / end | Pausar / reanudar / finalizar | Mettre en pause / reprendre / terminer | Pausieren / fortsetzen / beenden | Pausar / retomar / encerrar |
| Focus card / activate card | Enfocar ficha / activar ficha | Sélectionner la fiche affichée / activer la fiche | Karte in den Fokus nehmen / Karte aktivieren | Colocar ficha em foco / ativar ficha |
| Modeled scanner corridor | Corredor modelado del escáner | Corridor modélisé du scanner | Modellierter Scannerkorridor | Corredor modelado do scanner |
| Measured coverage | Cobertura medida | Couverture mesurée | Gemessene Abdeckung | Cobertura medida |
| Historical LiDAR returns | Retornos LiDAR históricos | Retours LiDAR historiques | Historische LiDAR-Echos | Retornos históricos de LiDAR |
| Original / derived | Original / derivado | Original / dérivé | Original / abgeleitet | Original / derivado |
| Height / elevation | Altura / elevación | Hauteur / altitude par rapport à une référence | Höhe / Höhe über einem Bezugsniveau | Altura / elevação |
| Source acquisition date | Fecha de adquisición de la fuente | Date d’acquisition de la source | Erfassungsdatum der Quelle | Data de aquisição da fonte |
| Vertical datum unknown | Datum vertical desconocido | Référentiel vertical inconnu | Vertikales Datum unbekannt | Datum vertical desconhecido |
| Local evaluation / organization approval | Evaluación local / aprobación de la organización | Évaluation locale / approbation de l’organisation | Lokale Bewertung / Organisationsgenehmigung | Avaliação local / aprovação da organização |
| Evidence submission / live sharing | Envío de pruebas / uso compartido en directo | Envoi de preuves / partage en direct | Nachweisübermittlung / Live-Freigabe | Envio de evidências / compartilhamento ao vivo |
| Permanent deletion | Eliminación permanente | Suppression définitive | Dauerhafte Löschung | Exclusão permanente |

Keep **DEM** and **DSM** as distinct scientific abbreviations. Explain bare-earth terrain versus a surface that may include structures/vegetation; neither is automatically measured by the simulator aircraft. Historical source date is distinct from simulated flight time. A modeled footprint never proves measured coverage. Retained observations, derived product references and server-approved campaign contributions have independent counts and authority. A translated display does not change units or original measurements.

## Reviewer receipt template

- Locale and regional variety:
- Qualified reviewer and relevant competence:
- Catalog version / canonical identity / source commit:
- Reviewed keys, surfaces and critical messages:
- Screenshot and real-device evidence:
- Corrections and unresolved phrases / explicit English fallbacks:
- Review date and status (partial / complete for named scope):

Human-review receipts are pending. Native Windows and installed MSFS receipts are separately Not Tested in this environment.
