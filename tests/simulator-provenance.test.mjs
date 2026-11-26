import test from 'node:test';
import assert from 'node:assert/strict';
import {simulatorSourceLabel} from '../src/AeroScience.Engine/wwwroot/simulator-origin.js';
import {connectionStatus} from '../src/AeroScience.Engine/wwwroot/simulator-settings.js';
import {localeBundle} from '../scripts/build/build-locales.mjs';
import {createTranslator} from '../src/AeroScience.Engine/wwwroot/i18n-format.js';

test('source presentation uses frozen evidence and never infers a legacy simulator from Settings',()=>{
  assert.equal(simulatorSourceLabel({version:1,simulatorId:'xplane12'},'simulator'),'X-Plane 12');
  assert.equal(simulatorSourceLabel({version:1,simulatorId:'msfs2024'},'simulator'),'Microsoft Flight Simulator 2024');
  assert.match(simulatorSourceLabel(null,'simulator'),/identity unknown/);
  assert.match(simulatorSourceLabel({version:99,simulatorId:'xplane12'},'simulator'),/identity unknown/);
  assert.match(simulatorSourceLabel({version:1,simulatorId:'xplane12'},'simulated'),/synthetic/i);
});
test('qualified connection messaging retains the pending installed-acceptance distinction',()=>{
  const snapshot={connection:{state:'connected'},telemetry:{},simulator:{id:'xplane12',productionRecordingReady:true,scienceIntegrationReady:true}};
  const text=connectionStatus(snapshot);assert.match(String(text),/Qualified flight recording/);assert.match(String(text),/Installed simulator acceptance is pending/);
  assert.doesNotMatch(String(text),/Collecting Science/);
});
test('new provenance and operation disclosures have explicit candidate English fallback in all approved languages',()=>{
  const bundle=localeBundle();
  for(const locale of bundle.manifest.locales){const translator=createTranslator({...bundle,locale:locale.id});
    for(const key of ['simulator.incomplete','simulator.qualifiedConnected','simulator.sourceBasis','simulator.legacyBasis']){
      assert.equal(translator.text(key),bundle.catalogs['en-US'].messages[key]);
      if(locale.id!=='en-US')assert.ok(bundle.catalogs[locale.id].pendingEnglishFallbackKeys.includes(key));
    }
  }
});
