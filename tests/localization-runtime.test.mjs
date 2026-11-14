import test from 'node:test';import assert from 'node:assert/strict';
import {localeBundle}from'../scripts/build/build-locales.mjs';
import{createTranslator,pluralCategory}from'../src/AeroScience.Engine/wwwroot/i18n-format.js';
import{message,setPresentationText,useLocale,t}from'../src/AeroScience.Engine/wwwroot/i18n.js';
import {instrumentSummaries,preparationPhase}from'../src/AeroScience.Engine/wwwroot/preparation-summary.js';
const data=localeBundle();
test('five candidate catalogs retain exact parameters and immutable original evidence in every locale',()=>{
 const original=Object.freeze({name:'Zoë 東京 <img> $&',utc:'2026-11-14T06:01:02.1234567Z',sha:'a'.repeat(64),value:'-1234.567890123 m',id:'NASA.Original.v1'}),before=JSON.stringify(original);
 for(const locale of data.manifest.locales){const tr=createTranslator(data,locale.id);assert.equal(tr.effectiveLocale,locale.id);for(const value of Object.values(original))assert.ok(tr.text('common.closeNamed',{name:value}).includes(value));}
 assert.equal(JSON.stringify(original),before);
});
test('integer plural categories implement all five approved language rules including zero and millions',()=>{
 for(const locale of ['en-US','de-DE','es-ES']){assert.equal(pluralCategory(locale,0),'other');assert.equal(pluralCategory(locale,1),'one');assert.equal(pluralCategory(locale,2),'other');}
 for(const locale of ['fr-FR','pt-BR']){assert.equal(pluralCategory(locale,0),'one');assert.equal(pluralCategory(locale,1),'one');assert.equal(pluralCategory(locale,2),'other');}
 for(const locale of ['es-ES','fr-FR','pt-BR']){assert.equal(pluralCategory(locale,1000000),'many');assert.equal(pluralCategory(locale,1000001),'other');}
 for(const locale of data.manifest.locales){const tr=createTranslator(data,locale.id);for(const count of [0,1,2,1000,1000000])assert.ok(tr.text('science.savedObservations',{count}).includes(String(count)));for(const count of [-1,1.5,'1',NaN])assert.equal(tr.text('science.savedObservations',{count}),data.catalogs['en-US'].messages['common.messageUnavailable']);}
});
test('explicit text bindings update in place without translating raw lookalike evidence or changing focus/state',()=>{
 const element=()=>({isConnected:true,textContent:'',attributes:{},setAttribute(k,v){this.attributes[k]=String(v);}});
 const label=element(),raw=element(),attribute=element(),draft={value:'Unmodified pilot draft',commands:0,revision:71};
 useLocale('en-US');setPresentationText(label,message('nav.missions'));setPresentationText(raw,'Missions');setPresentationText(attribute,message('common.closeNamed',{name:'Zoë <img> $&'}),'aria-label');
 const root={childNodes:[label,raw,attribute]},before=JSON.stringify(draft);useLocale('de-DE',{root});assert.equal(label.textContent,t('nav.missions'));assert.notEqual(label.textContent,'Missions');assert.equal(raw.textContent,'Missions');assert.ok(attribute.attributes['aria-label'].includes('Zoë <img> $&'));assert.equal(JSON.stringify(draft),before);
 setPresentationText(label,'Original source value');useLocale('fr-FR',{root});assert.equal(label.textContent,'Original source value');useLocale('en-US');
});
test('per-message resource corruption and mismatched source identities recover to canonical English offline',()=>{
 for(const message of ['',null,{},'{wrong}',{one:'wrong'}]){const copy=structuredClone(data);copy.catalogs['fr-FR'].messages['common.closeNamed']=message;const tr=createTranslator(copy,'fr-FR');assert.equal(tr.text('common.closeNamed',{name:'Original'}),'Close Original');}
 for(const change of [c=>c.version++,c=>c.sourceHash='stale',c=>c.locale='de-DE']){const copy=structuredClone(data);change(copy.catalogs['fr-FR']);const tr=createTranslator(copy,'fr-FR');assert.equal(tr.effectiveLocale,'en-US');assert.equal(tr.text('nav.missions'),'Missions');}
});

test('locale selection cannot change authoritative preparation phases, transfer classification or retained byte counts',()=>{
 const now=new Date().toISOString(),rows=Object.freeze([Object.freeze({kind:'terrain-scanner',status:'Ready',downloadedBytes:16,reusedBytes:32,updatedUtc:now}),Object.freeze({kind:'terrain-scanner',status:'Downloading',stage:'Receiving',downloadedBytes:128,reusedBytes:0,updatedUtc:now})]),before=JSON.stringify(rows);
 for(const locale of data.manifest.locales){useLocale(locale.id);assert.equal(preparationPhase(rows[1]),'Downloading');const [summary]=instrumentSummaries(rows);assert.equal(summary.status,'Partially Ready');assert.equal(summary.activelyDownloading,true);assert.equal(summary.downloadedBytes,144);assert.equal(summary.reusedBytes,32);assert.equal(summary.ready,1);assert.equal(JSON.stringify(rows),before);}useLocale('en-US');
});
