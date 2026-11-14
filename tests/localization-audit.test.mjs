import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import{fileURLToPath}from'node:url';
import{execFileSync}from'node:child_process';
import {inventoryJavaScript} from '../scripts/build/audit-localization.mjs';
import {strictJson,validUnicode,validateQuality,sourceFingerprint,messageFingerprint} from '../scripts/build/localization-quality.mjs';
import {protectedTranslation,unboundPresentationHelpers} from '../scripts/build/audit-display-bindings.mjs';

test('presentation helper calls require an import or declaration before packaging',()=>{
 assert.deepEqual(unboundPresentationHelpers("label.append(input,presentationTextNode(copy));").map(r=>r.name),['presentationTextNode']);
 assert.equal(unboundPresentationHelpers("import {presentationTextNode} from './i18n.js';label.append(input,presentationTextNode(copy));").length,0);
 assert.equal(unboundPresentationHelpers("function render(presentationTextNode){return presentationTextNode(copy);}").length,0);
});

test('audit CLI creates diagnostics on a fresh checkout and still rejects protected translations',()=>{
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'aeroscience-localization-audit-'));
 const script=fileURLToPath(new URL('../scripts/build/audit-display-bindings.mjs',import.meta.url));
 try{
  for(const directory of ['src/AeroScience.Engine/wwwroot/i18n/locales','src/AeroScience.Organization/wwwroot','msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience'])fs.mkdirSync(path.join(root,directory),{recursive:true});
  fs.writeFileSync(path.join(root,'src/AeroScience.Engine/wwwroot/i18n/locales/en-US.json'),JSON.stringify({messages:{'test.method':'POST'}}));
  assert.equal(fs.existsSync(path.join(root,'artifacts')),false);
  const run=()=>execFileSync(process.execPath,[script],{cwd:root,encoding:'utf8',stdio:'pipe'});
  assert.equal(JSON.parse(run()).protectedReferences,0);
  const receipt=path.join(root,'artifacts/p07-protected-display-bindings.json');assert.deepEqual(JSON.parse(fs.readFileSync(receipt,'utf8')),[]);
  fs.writeFileSync(path.join(root,'src/AeroScience.Engine/wwwroot/fixture.js'),"import {t as textForUI} from './i18n.js';const command={method:textForUI('test.method')};");
  assert.throws(run,error=>error.status===1);const rejected=JSON.parse(fs.readFileSync(receipt,'utf8'));assert.equal(rejected.length,1);assert.equal(rejected[0].key,'test.method');
 }finally{fs.rmSync(root,{recursive:true,force:true});}
});

test('nested display prose is inventoried while original state comparisons remain identifiers',()=>{
 const rows=inventoryJavaScript(`localizedCopy('flight.status',{detail:state==='Armed'?'Waiting for the trigger.':'No collection is running.'});`,'src/AeroScience.Engine/wwwroot/nested-fixture.js');
 for(const text of ['Waiting for the trigger.','No collection is running.'])assert.equal(rows.find(r=>r.text===text)?.decision,'presentation');
 assert.equal(rows.find(r=>r.text==='Armed')?.decision,'protected-identifier');
 assert.equal(inventoryJavaScript(`value.normalize('NFD');`,'source.js').find(r=>r.text==='NFD').decision,'protected-identifier');
 assert.equal(protectedTranslation(`value.normalize(textForUI('test.normalization'));`,{'test.normalization':'NFD'}).length,1);
});

test('scientific export headers, nested unknown values and evidence classes never acquire translations',()=>{
 const messages={header:'Original value',unknown:'Unknown',kind:'Modeled scanner support, not observed coverage',label:'Download analysis'};
 const source=`button(root,localizedCopy('label'),()=>{const rows=[[textForUI('header')],...samples.map(s=>[s.utc??textForUI('unknown')])];rows.push([textForUI('kind')]);downloadAnalysis({evidenceClass:textForUI('kind')});downloadAnalysis(rows);});`;
 const rows=protectedTranslation(source,messages);
 assert.deepEqual(rows.map(r=>r.key).sort(),['header','kind','kind','unknown']);
 assert.ok(!rows.some(r=>r.key==='label'));
 const raw=`button(root,'Download analysis',()=>{const rows=[['Original value'],...samples.map(s=>[s.utc??'Unknown'])];rows.push(['Modeled scanner support, not observed coverage']);downloadAnalysis(rows);});`;
 const inventory=inventoryJavaScript(raw,'src/AeroScience.Engine/wwwroot/export-fixture.js');
 for(const text of ['Original value','Unknown','Modeled scanner support, not observed coverage'])assert.equal(inventory.find(r=>r.text===text)?.decision,'protected-value',text);
 assert.equal(inventory.find(r=>r.text==='Download analysis')?.decision,'presentation');
});

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
