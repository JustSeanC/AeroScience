import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFileSync,cpSync,mkdtempSync,renameSync,rmSync,existsSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {parse} from 'acorn';
import {nativeCatalogAssets,localeBundle} from '../scripts/build/build-locales.mjs';
import {compileNative,compileStage} from '../scripts/build/native-javascript.mjs';
const catalogVersion=localeBundle().manifest.catalogVersion;
const adapter=readFileSync('msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/AeroScience.I18n.js','utf8');
function load(bundle=localeBundle()){
 const context=vm.createContext({Intl:undefined});
 for(const [name,source]of Object.entries(nativeCatalogAssets(bundle))){const compiled=compileNative(source,name);parse(compiled,{ecmaVersion:2017});vm.runInContext(compiled,context);}
 vm.runInContext('Object.hasOwn=undefined;',context);vm.runInContext(compileNative(adapter),context);return context;
}
test('classic ES2017 native catalogs run without Intl or Object.hasOwn and preserve text parameters',()=>{
 const c=load();
 for(const [locale,mission] of [['en-US','Mission'],['es-ES','Misión'],['fr-FR','Mission'],['de-DE','Mission'],['pt-BR','Missão']]){
  const result=c.AeroScienceI18n.use({version:1,catalogVersion,locale});assert.equal(result.effectiveLocale,locale);assert.equal(result.fallback,false);
  if(locale!=='en-US'){assert.ok(result.message.includes(localeBundle().manifest.locales.find(l=>l.id===locale).name));assert.ok(!result.message.includes('uses English'));}
  assert.equal(c.AeroScienceI18n.text('toolbar.mission','fallback'),mission);
 }
 const name='Zoë 東京 <img> $& µm';assert.equal(c.AeroScienceI18n.text('common.closeNamed','Close',{name}),'Fechar '+name);
});
test('old companions, unknown locales and catalog/contract mismatches fall back to bundled English',()=>{
 const c=load();for(const metadata of [null,{}, {version:1,catalogVersion:catalogVersion-1,locale:'fr-FR'}, {version:2,catalogVersion,locale:'fr-FR'},{version:1,catalogVersion:999,locale:'fr-FR'},{version:1,catalogVersion,locale:'xx'}]){
  const state=c.AeroScienceI18n.use(metadata);assert.equal(state.fallback,true);assert.equal(state.effectiveLocale,'en-US');assert.match(state.message,/English fallback/);assert.equal(c.AeroScienceI18n.text('toolbar.payload','fallback'),'Payload');
 }
});
test('missing or inconsistent staged catalog never removes the original critical label',()=>{
 for(const setup of ['', 'var AeroScienceLocaleData={};','var AeroScienceLocaleData={manifest:{version:7}};']){
  const c=vm.createContext({Intl:undefined});vm.runInContext(setup+compileNative(adapter),c);
  assert.equal(c.AeroScienceI18n.use(null).fallback,true);assert.equal(c.AeroScienceI18n.text('science.unknown','Science status unavailable'),'Science status unavailable');
 }
});
test('SDK staging ships and records canonical catalogs in both original and renamed validation panels',()=>{
 const temporary=mkdtempSync(join(tmpdir(),'aeroscience-p02b-stage-'));
 try{for(const name of ['AeroScience','AeroScienceValidationV070']){
  const stage=join(temporary,name);cpSync('msfs-toolbar',stage,{recursive:true});const panels=join(stage,'PackageSources/html_ui/InGamePanels');
  if(name!=='AeroScience')renameSync(join(panels,'AeroScience'),join(panels,name));
  const receipt=compileStage(stage);
  for(const asset of ['AeroScience.LocaleData.js','AeroScience.LocaleFormat.js','AeroScience.I18n.js','AeroScience.FlightStatus.js']){
   const file=join(panels,name,asset);assert.ok(existsSync(file));parse(readFileSync(file,'utf8'),{ecmaVersion:2017});
   assert.ok(receipt.files.some(f=>f.path.endsWith('/'+name+'/'+asset)));
  }
  const context=vm.createContext({Intl:undefined});for(const asset of ['AeroScience.LocaleData.js','AeroScience.LocaleFormat.js','AeroScience.I18n.js'])vm.runInContext(readFileSync(join(panels,name,asset),'utf8'),context);
  assert.equal(context.AeroScienceI18n.use({version:1,catalogVersion,locale:'de-DE'}).effectiveLocale,'de-DE');
 }}finally{rmSync(temporary,{recursive:true,force:true});}
});
