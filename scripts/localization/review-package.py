"""Reproduce the candidate message/context review tables without reading owner data."""
import csv
import json
from pathlib import Path

base = Path('src/AeroScience.Engine/wwwroot/i18n')
output = Path('docs/roadmap/p07-review')
output.mkdir(parents=True, exist_ok=True)
english = json.loads((base / 'locales/en-US.json').read_text())
schema = json.loads((base / 'message-schema.json').read_text())
quality = json.loads(Path('artifacts/p07-candidate-qa.json').read_text())
if quality['version'] != english['version']:
    raise SystemExit('Assemble and validate the current candidate catalogs first.')
for locale in ['es-ES', 'fr-FR', 'de-DE', 'pt-BR']:
    catalog = json.loads((base / 'locales' / (locale + '.json')).read_text())
    with (output / ('messages.' + locale + '.tsv')).open('w', newline='', encoding='utf-8') as stream:
        writer = csv.writer(stream, delimiter='\t')
        writer.writerow(['key', 'surface', 'critical', 'context', 'parameters', 'canonical_English', 'bundled_candidate', 'explicit_English_fallback_reason', 'human_review'])
        for key, message in english['messages'].items():
            rule = schema['messages'][key]
            writer.writerow([key, rule.get('surface', ''), rule.get('critical', False), rule['context'], ','.join(rule['parameters']), json.dumps(message, ensure_ascii=False), json.dumps(catalog['messages'][key], ensure_ascii=False), quality['pending'][locale].get(key, ''), 'PENDING'])
report = {'catalogVersion': english['version'], 'catalogIdentity': quality['sourceHash'], 'keyCoverage': {locale: len(english['messages']) for locale in ['en-US', 'es-ES', 'fr-FR', 'de-DE', 'pt-BR']}, 'candidateReceipts': quality['receipts'], 'fallbacks': quality['pending'], 'renderedCoverage': 'Sampled browser receipts are separate; a complete catalog does not prove all rendered views.', 'humanReview': 'PENDING', 'installedWindows': 'NOT TESTED', 'installedMSFS': 'NOT TESTED'}
(output / 'p07b-candidate-coverage.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
print('Wrote four complete candidate/context review tables and explicit fallback report.')
