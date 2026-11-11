import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,mkdtempSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {execFileSync} from 'node:child_process';
import vm from 'node:vm';
import {parse} from 'acorn';
import {localeBundle,validateCatalogs,validateReferences,generatedModule,pseudoCatalog} from '../scripts/build/build-locales.mjs';
import {createTranslator} from '../src/AeroScience.Engine/wwwroot/i18n-format.js';
import {resolveRoute,splitFragment} from '../src/AeroScience.Engine/wwwroot/navigation-routes.js';
import {settingsLocation} from '../src/AeroScience.Engine/wwwroot/settings-navigation.js';
import {missionPresentation} from '../src/AeroScience.Engine/wwwroot/presentation-state.js';
const copy=value=>JSON.parse(JSON.stringify(value));
const bundle=localeBundle();

test('the approved locale manifest and canonical generated bundle are complete and deterministic',()=>{
  const report=validateCatalogs(bundle);
  assert.deepEqual(bundle.manifest.locales.map(locale=>locale.id),['en-US','es-ES','fr-FR','de-DE','pt-BR']);
  assert.deepEqual(report.bundled,['en-US']);assert.ok(report.messages>=70);
  assert.equal(readFileSync('src/AeroScience.Engine/wwwroot/i18n/catalog.generated.js','utf8').replace(/\r\n/g,'\n'),generatedModule(bundle));
});
test('literal template, helper and navigation message references cannot silently drift from the catalog',()=>{
  assert.throws(()=>validateReferences(bundle.schema,{'index.html':'<a data-i18n="missing.navigation">Fallback</a>'}),/Undefined message/);
  assert.throws(()=>validateReferences(bundle.schema,{'helper.js':"import {t} from './i18n.js';t('missing.warning')"}),/Undefined message/);
  const report=validateReferences(bundle.schema,{'index.html':'<a data-i18n="nav.settings">Settings</a>'});
  assert.equal(report.references,1);assert.ok(report.unusedKeys.includes('science.collecting'));
});
const malformed=[
  ['missing critical English',data=>delete data.catalogs['en-US'].messages['dialog.unsavedWarning']],
  ['extra key',data=>data.catalogs['en-US'].messages['unsafe.extra']='Unexpected'],
  ['changed placeholder',data=>data.catalogs['en-US'].messages['common.closeNamed']='Close {wrong}'],
  ['empty warning',data=>data.catalogs['en-US'].messages['dialog.unsavedWarning']=''],
  ['missing plural other',data=>delete data.catalogs['en-US'].messages['science.savedObservations'].other],
  ['unknown plural case',data=>data.catalogs['en-US'].messages['science.savedObservations'].few='{count} observations'],
  ['unreviewed language advertised',data=>data.manifest.locales[1].review='reviewed'],
  ['catalog version drift',data=>data.catalogs['en-US'].version=99],
  ['locale substitution',data=>data.manifest.locales[4].id='pt-PT'],
  ['unsupported interpolation',data=>data.catalogs['en-US'].messages['common.closeNamed']='Close {{name}}']
];
for(const [name,mutate] of malformed)test('catalog validation rejects '+name,()=>{
  const data=copy(bundle);mutate(data);assert.throws(()=>validateCatalogs(data));
});
test('preview, unsupported and stale catalogs fall back to readable English with English plural rules',()=>{
  for(const locale of ['es-ES','fr-FR','de-DE','pt-BR','zz-ZZ']){
    const translator=createTranslator(bundle,locale);assert.equal(translator.effectiveLocale,'en-US');
    assert.equal(translator.text('science.savedObservations',{count:0}),'0 saved observations');
    assert.equal(translator.text('science.savedObservations',{count:1}),'1 saved observation');
    assert.equal(translator.text('science.savedObservations',{count:2}),'2 saved observations');
  }
  const data=copy(bundle);data.catalogs['fr-FR']={version:99,locale:'fr-FR',messages:{'nav.settings':'Wrong version'}};
  assert.equal(createTranslator(data,'fr-FR').text('nav.settings'),'Settings');
  const production=createTranslator(bundle);
  assert.ok(production.text('missing.safety').length>0);assert.ok(!production.text('constructor').includes('constructor'));
  assert.equal(production.text('common.closeNamed'),bundle.catalogs['en-US'].messages['common.messageUnavailable']);
  const missing=[];assert.equal(createTranslator(bundle,'en-US',{development:true,onMissing:key=>missing.push(key)}).text('missing.key'),'[missing.key]');
  assert.deepEqual(missing,['missing.key']);
});
test('parameter substitution preserves proper names, scientific units, UTC, hashes and literal replacement syntax',()=>{
  const value='Zoë 東京 $& <img src=x> · µm ³ 2026-11-10T12:34:56.789Z sha256:abcdef';
  assert.equal(createTranslator(bundle,'de-DE').text('common.closeNamed',{name:value}),'Close '+value);
  assert.equal(createTranslator(bundle).text('science.savedObservations',{count:NaN}),bundle.catalogs['en-US'].messages['common.messageUnavailable']);
  for(const count of [-1,1.5,'0'])assert.equal(createTranslator(bundle).text('science.savedObservations',{count}),bundle.catalogs['en-US'].messages['common.messageUnavailable']);
  const pseudo=pseudoCatalog(bundle.catalogs['en-US']);
  assert.ok(pseudo.messages['nav.settings'].length>bundle.catalogs['en-US'].messages['nav.settings'].length*1.3);
  assert.ok(pseudo.messages['common.closeNamed'].includes('{name}'));
  assert.equal(pseudo.locale,'en-XA');assert.ok(!bundle.manifest.locales.some(locale=>locale.id==='en-XA'));
});
test('generated native formatter parses as ES2017 and works without Intl or modern Object.hasOwn',()=>{
  const output=mkdtempSync(join(tmpdir(),'aeroscience-locales-'));
  try{
    execFileSync(process.execPath,['scripts/build/build-locales.mjs','--check','--output-directory',output]);
    const source=readFileSync(join(output,'AeroScience.I18n.js'),'utf8'),data=readFileSync(join(output,'AeroScience.LocaleData.js'),'utf8');
    parse(source,{ecmaVersion:2017});parse(data,{ecmaVersion:2017});
    const context=vm.createContext({});vm.runInContext('Intl=undefined;Object.hasOwn=undefined;'+data+source,context);
    assert.equal(vm.runInContext('AeroScienceI18n.createTranslator(AeroScienceLocaleData,"fr-FR").text("science.savedObservations",{count:0})',context),'0 saved observations');
    assert.deepEqual(JSON.parse(readFileSync(join(output,'catalog-bundle.json'),'utf8')),bundle);
  }finally{rmSync(output,{recursive:true,force:true});}
});

test('legacy routes and full query values keep their original renderer',()=>{
  const cases={'#control':'control','#operations':'operations','#planner':'planner','#history':'history','#results':'history',
    '#science':'instruments','#instruments':'instruments','#my-research':'my-research','#settings':'settings',
    '#library':'library','#library/download':'library','#library/storage':'library-storage','#downloads':'downloads','#help':'help',
    '#workspace/map':'science-workspace','#workspace/compare':'science-workspace','#workspace/analysis':'science-workspace',
    '#workspace/results':'science-workspace','#workspace/replay':'science-workspace','#unknown':'my-research'};
  for(const [hash,page] of Object.entries(cases)){
    const route=resolveRoute(hash+'?future=a%3Fb%26c&instrument=thermal-imager');
    assert.equal(route.page,page,hash);assert.equal(route.redirect,null,hash);assert.equal(route.query.get('future'),'a?b&c');
  }
  assert.equal(splitFragment('#settings/units?future=a?b').search,'future=a?b');
  assert.equal(settingsLocation('#settings/units?future=a?b').query.get('future'),'a?b');
  assert.equal(settingsLocation('#settings/unknown').category,'general');
  assert.equal(resolveRoute('#operations?flight=archived').archivedFlight,'archived');
  assert.equal(resolveRoute('#operations?researchPlan=next').archivedFlight,null);
});
test('canonical and historical redirects retain parameters and never turn archived review into active flight',()=>{
  const aliases={'#missions':'my-research','#missions/plans':'planner','#missions/templates':'my-research',
    '#flight':'operations','#flight/details':'control','#flight/instruments':'instruments',
    '#results/overview':'science-workspace','#results/explore/map':'science-workspace',
    '#results/explore/compare':'science-workspace','#results/explore/replay':'science-workspace','#settings/support':'help'};
  for(const [hash,page] of Object.entries(aliases)){
    const route=resolveRoute(hash+'?researchPlan=p&future=a%3Fb&guide=remote-sensing%2Findex.md');
    assert.equal(route.page,page,hash);const destination=resolveRoute(route.redirect);
    assert.equal(destination.page,page);assert.equal(destination.query.get('researchPlan'),'p');
    assert.equal(destination.query.get('future'),'a?b');assert.equal(destination.redirect,null);
  }
  for(const hash of ['#workspace/timeline','#results/evidence']){
    const route=resolveRoute(hash+'?missions=original,second&card=c&fromUtc=2026-11-10T00%3A00%3A00Z&future=x');
    const destination=resolveRoute(route.redirect);assert.equal(destination.page,'operations');
    assert.equal(destination.archivedFlight,'original');assert.equal(destination.query.get('missions'),'original,second');
    assert.equal(destination.query.get('card'),'c');assert.equal(destination.query.get('future'),'x');
  }
  assert.equal(resolveRoute(resolveRoute('#results/evidence').redirect).page,'history');
  assert.equal(resolveRoute('#home').known,true);assert.equal(resolveRoute('').page,'home');assert.equal(resolveRoute('#organizations').known,true);
  const org=resolveRoute(resolveRoute('#organizations?permission=shareLiveFlight&future=x').redirect);assert.equal(org.page,'my-research');assert.equal(org.query.get('section'),'organizations');assert.equal(org.query.get('permission'),'shareLiveFlight');assert.equal(org.query.get('future'),'x');
});
test('presentation state keeps recording independent, fails closed when stale/unbound and never mutates authority',()=>{
  const now=Date.parse('2026-11-10T12:00:00Z');
  const data={generatedUtc:new Date(now).toISOString(),flight:{activeMission:{id:'flight'},connection:{state:'connected'}},
    collection:{state:{missionId:'flight',phase:'armed',currentSessionId:null},actions:[{action:'startData',available:true},{action:'pauseData',available:false}]}};
  const before=JSON.stringify(data);const armed=missionPresentation(data,{now});
  assert.equal(armed.recording,'active');assert.equal(armed.science,'armed');assert.deepEqual(armed.scienceActions,['startData']);
  assert.equal(JSON.stringify(data),before);assert.ok(Object.isFrozen(armed));assert.ok(Object.isFrozen(armed.scienceActions));
  assert.deepEqual(missionPresentation(data,{now,pending:true}).scienceActions,[]);
  for(const age of [6001,-2001]){const result=missionPresentation(data,{now:now+age});assert.equal(result.recording,'unknown');assert.equal(result.science,'unknown');assert.deepEqual(result.scienceActions,[]);}
  const collecting=copy(data);collecting.collection.state.phase='collecting';
  assert.equal(missionPresentation(collecting,{now}).science,'unknown');
  collecting.collection.state.currentSessionId='session';assert.equal(missionPresentation(collecting,{now}).science,'collecting');
  collecting.collection.fault='Storage unavailable';assert.equal(missionPresentation(collecting,{now}).science,'unavailable');
  collecting.collection.state.missionId='older-flight';assert.deepEqual(missionPresentation(collecting,{now}).scienceActions,[]);
  const paused=copy(data);paused.collection.state.phase='paused';assert.equal(missionPresentation(paused,{now}).recording,'active');
  assert.equal(missionPresentation(paused,{now}).science,'paused');
  assert.equal(missionPresentation({generatedUtc:data.generatedUtc},{now}).recording,'unknown');
  assert.equal(missionPresentation({generatedUtc:data.generatedUtc,flight:{}},{now}).science,'unknown');
  const malformed=copy(data);malformed.collection.actions=[null,{action:'startData',available:'yes'}];
  assert.deepEqual(missionPresentation(malformed,{now}).scienceActions,[]);
  malformed.collection.actions='unsupported';assert.deepEqual(missionPresentation(malformed,{now}).scienceActions,[]);
});
