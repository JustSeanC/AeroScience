import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {recordingAllowed} from '../src/AeroScience.Engine/wwwroot/simulator-settings.js';
import {missionPresentation} from '../src/AeroScience.Engine/wwwroot/presentation-state.js';
import {currentFlightPresentation} from '../src/AeroScience.Engine/wwwroot/current-flight-state.js';
import {createTranslator} from '../src/AeroScience.Engine/wwwroot/i18n-format.js';
import {localeBundle} from '../scripts/build/build-locales.mjs';

test('X-Plane presentation cannot infer collection permission from a connected synthetic fixture',()=>{
  const now=Date.now(),id='flight',state={activeMission:{id},simulator:{id:'xplane12',productionRecordingReady:false,scienceIntegrationReady:false},connection:{state:'connected'},telemetry:{timestampUtc:new Date(now).toISOString()}};
  const data={version:1,generatedUtc:new Date(now).toISOString(),flight:state,collection:{state:{missionId:id,phase:'collecting',currentSessionId:'session'},currentSession:{id:'session'},actions:[{action:'startData',available:true}]}};
  assert.equal(recordingAllowed(state),false);assert.equal(missionPresentation(data,{now}).science,'unavailable');assert.deepEqual(missionPresentation(data,{now}).scienceActions,[]);
  assert.deepEqual(currentFlightPresentation(data,state,{now}).scienceActions,[]);assert.equal(recordingAllowed({connection:{state:'connected'}}),true);
});
test('candidate languages keep the new safety text in explicit English fallback without changing previous translations',()=>{
  const bundle=localeBundle();
  for(const locale of bundle.manifest.locales){const translator=createTranslator(bundle,locale.id);
    assert.equal(translator.text('simulator.recordingDisabled'),bundle.catalogs['en-US'].messages['simulator.recordingDisabled']);
    assert.equal(translator.text('simulator.restart'),bundle.catalogs['en-US'].messages['simulator.restart']);
    if(locale.id!=='en-US')assert.ok(bundle.catalogs[locale.id].pendingEnglishFallbackKeys.includes('simulator.recordingDisabled'));
  }
});
test('native source selection is accessible, process-frozen and avoids XP Community discovery/setup',()=>{
  const main=readFileSync('src/AeroScience.Desktop/MainForm.cs','utf8'),panel=readFileSync('src/AeroScience.Desktop/SimulatorPanel.cs','utf8'),wizard=readFileSync('src/AeroScience.Desktop/SetupWizardForm.cs','utf8');
  assert.match(main,/ActiveSimulator \{ get; \}/);assert.match(main,/EngineOptions\(preferences with\{Simulator=ActiveSimulator\}\)/);
  assert.match(panel,/DropDownStyle=ComboBoxStyle.DropDownList/);assert.match(panel,/source.AccessibleName/);assert.match(panel,/port.AccessibleName/);
  assert.match(wizard,/if\(app.UsesMsfs\)\s*\{\s*community.Items.AddRange\(ToolbarInstaller.DiscoverCommunityFolders/);
  assert.match(main,/if \(!UsesMsfs\).*simulator.toolbarUnavailable/);
});
