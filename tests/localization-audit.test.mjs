import test from 'node:test';
import assert from 'node:assert/strict';
import {inventoryJavaScript} from '../scripts/build/audit-localization.mjs';
import {strictJson,validUnicode,validateQuality,sourceFingerprint,messageFingerprint} from '../scripts/build/localization-quality.mjs';
import {protectedTranslation} from '../scripts/build/audit-display-bindings.mjs';

test('inventory distinguishes presentation from command IDs, style, original rows and user field values',()=>{
 const src=`node('p','No valid source is available.'); button(root,'Pause Science',run,'button primary'); field(root,'Mission name','text','Pilot original name'); table(root,'Original readings',['Recorded UTC','Value'],[['Original evidence text','12.345 m']]); api('/api/mission/control',{action:'startData',id:'Original.Source.v1'}); node('p',user.name);`;
 const rows=inventoryJavaScript(src,'src/AeroScience.Engine/wwwroot/example.js');
 for(const text of ['No valid source is available.','Pause Science','Mission name','Original readings','Recorded UTC','Value'])assert.equal(rows.find(r=>r.text===text)?.decision,'presentation',text);
 for(const text of ['button primary','Pilot original name','Original evidence text','12.345 m'])assert.ok(rows.find(r=>r.text===text)?.decision.startsWith('protected'),text);
 assert.ok(!rows.some(r=>r.text==='user.name'));
});
test('strict catalog parsing rejects duplicate message keys and malformed Unicode without normalizing evidence',()=>{
 assert.throws(()=>strictJson('{"messages":{"nav.home":"Home","nav.home":"Changed"}}'),/Duplicate JSON key/);
 assert.equal(strictJson('{"value":"Zoë 東京 µm $&"}').value,'Zoë 東京 µm $&');
 for(const value of ['\uD800','\uDC00','bad\u0000value','\u202Ehidden'])assert.equal(validUnicode(value),false);
 assert.equal(validUnicode('Français\nPortuguês — ³ \u{1F6E9}'),true);
});
test('translation bindings reject HTTP verbs, typed fields, option identities and CSS while allowing actual option labels',()=>{
 const messages={'test.verb':'POST','test.role':'Reviewer','test.label':'Scientific reviewer','test.css':'finding-card payload-recommendation','test.state':'Ready'};
 const source=`const method=textForUI('test.verb');const body={status:textForUI('test.state')};select(parent,'Role',[[textForUI('test.role'),localizedCopy('test.label')]],role);text('article',undefined,textForUI('test.css'));`;
 const rows=protectedTranslation(source,messages);assert.deepEqual(rows.map(r=>r.key).sort(),['test.css','test.role','test.state','test.verb']);assert.ok(!rows.some(r=>r.key==='test.label'));
 assert.equal(protectedTranslation(`select(parent,'Role',[['Reviewer',localizedCopy('test.label')],['Pilot',localizedCopy('test.label')]],role);`,messages).length,0);
});
test('inventory resolves scoped helper signatures without translating selected values or form input data',()=>{
 const source=`select(root,'Shared caption',[['Ready','Ready label']],'Ready');function editor(){function select(label,options,value){const control=document.createElement('select');return control;}select('Local caption',[['Ready','Local ready label']],'Ready');}`;
 const rows=inventoryJavaScript(source,'src/AeroScience.Engine/wwwroot/scoped-fixture.js');
 for(const text of ['Shared caption','Ready label','Local caption','Local ready label'])assert.equal(rows.find(row=>row.text===text)?.decision,'presentation',text);
 assert.ok(rows.filter(row=>row.text==='Ready').every(row=>row.decision.startsWith('protected')));
});
test('source tracking rejects copied critical English, stale translations and fabricated review',()=>{
 const english={'warning.delete':'Permanently delete {count} files?'},hash=sourceFingerprint(english);
 const data={manifest:{qualityContract:1,catalogIdentity:hash,locales:[{id:'en-US',review:'source'},{id:'es-ES',review:'candidate'}]},schema:{messages:{'warning.delete':{critical:true,parameters:['count'],maximumLength:100}}},catalogs:{'en-US':{messages:english},'es-ES':{messages:{'warning.delete':'¿Eliminar permanentemente {count} archivos?'},sourceHash:hash,sourceMessages:{'warning.delete':messageFingerprint(english['warning.delete'])}}}};
 assert.equal(validateQuality(data).coverage['es-ES'].humanReviewed,false);
 data.catalogs['es-ES'].messages={...english};assert.throws(()=>validateQuality(data),/Copied English|Untranslated critical/);
 data.catalogs['es-ES'].pendingEnglishFallbackKeys=['warning.delete'];assert.equal(validateQuality(data).coverage['es-ES'].explicitEnglishFallbackKeys.length,1);
 data.catalogs['es-ES'].sourceMessages['warning.delete']='outdated';assert.throws(()=>validateQuality(data),/Stale message source/);
 data.manifest.locales[1].review='reviewed';assert.throws(()=>validateQuality(data),/qualified human review/);
});

test('native command membership cannot use a translated action even when it resembles a two-label tuple',()=>{
 const source=`const labels=[['beginAttempt',nativeText('label','Begin attempt')]]; const allowed=['beginAttempt',nativeText('command','enableAutomatic')].includes(action);`;
 const found=protectedTranslation(source,{label:'Begin attempt',command:'enableAutomatic'});
 assert.equal(found.length,1);assert.equal(found[0].key,'command');assert.match(found[0].reason,/identity search/);
});

test('namespace adapters consume stable key suffixes rather than translated presentation text',()=>{
 const source=`const text=key=>t('results.'+key); text(localizedCopy('incorrect',{name:kind}));`;
 const found=protectedTranslation(source,{incorrect:'{name}Layer'});
 assert.equal(found.length,1);assert.match(found[0].reason,/Protected helper value/);
 const inventory=inventoryJavaScript(`const text=key=>t('results.'+key); text(key+'Layer');`,'src/AeroScience.Engine/wwwroot/survey-results.js');
 assert.equal(inventory.find(row=>row.text==='Layer').decision,'protected-value');
});

test('same-version translated resource edits invalidate the complete bundle identity even when English is unchanged',async()=>{
 const{localeBundle}=await import('../scripts/build/build-locales.mjs');const data=localeBundle();data.catalogs['de-DE'].messages['native.armed']='Bereit (geändert)';
 assert.throws(()=>validateQuality(data),/Stale complete locale bundle identity/);
});
